// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Media;
using Bootrix.App.Services;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Text;
using Bootrix.Core.Tiny;
using Bootrix.Core.Writing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Bootrix.App.ViewModels;

public sealed partial class TinyViewModel : ObservableObject, IDisposable
{
    private readonly IEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly MediaPlanService _planner;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly ILogger<TinyViewModel> _logger;
    private CancellationTokenSource? _inspect;
    private CancellationTokenSource? _soft;
    private CancellationTokenSource? _abort;

    public TinyViewModel(
        IEngine engine,
        IDialogService dialogs,
        MediaPlanService planner,
        SettingsStore settings,
        Localizer localizer,
        ILogger<TinyViewModel> logger)
    {
        _engine = engine;
        _dialogs = dialogs;
        _planner = planner;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;

        _cancelText = localizer.Get("Write.Cancel");
        BuildProfiles();
        _selectedProfile = Profiles[0];
        BuildGroups();
        localizer.CultureChanged += (_, _) =>
        {
            var id = SelectedProfile?.Id;
            BuildProfiles();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles[0];
            BuildGroups();
        };
    }

    public ObservableCollection<TinyProfileOption> Profiles { get; } = [];

    public ObservableCollection<TinyEditionOption> Editions { get; } = [];

    public ObservableCollection<TinyGroupItem> Groups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string? _sourcePath;

    [ObservableProperty]
    private string _sourceMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsAcknowledgement))]
    [NotifyPropertyChangedFor(nameof(OffersHardwareBypass))]
    private TinyProfileOption? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private TinyEditionOption? _selectedEdition;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _outputPath;

    [ObservableProperty]
    private string _volumeLabel = "TINY";

    [ObservableProperty]
    private bool _skipHardwareChecks = true;

    [ObservableProperty]
    private bool _forFat32;

    [ObservableProperty]
    private string _localAccount = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private bool _acknowledgeNoServicing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickOutputCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _progressTitle = "";

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private string _remainingText = "";

    [ObservableProperty]
    private string _cancelText;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultMessage = "";

    [ObservableProperty]
    private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    public void Dispose()
    {
        _inspect?.Cancel();
        _inspect?.Dispose();
    }

    public bool IsIdle => !IsBusy;

    public bool HasSource => !string.IsNullOrWhiteSpace(SourcePath);

    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputPath);

    public bool NeedsAcknowledgement => SelectedProfile?.BreaksServicing == true;

    public bool OffersHardwareBypass => SelectedProfile?.IsWindows11 == true;

    partial void OnSelectedProfileChanged(TinyProfileOption? value)
    {
        BuildGroups();
        AcknowledgeNoServicing = false;
    }

    public void SetSource(string path)
    {
        SourcePath = path;
        _ = InspectSourceAsync(path);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void PickSource()
    {
        if (_dialogs.PickIso(_settings.Current.LastImageDirectory) is { } path)
        {
            SetSource(path);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void PickOutput()
    {
        var name = Path.GetFileNameWithoutExtension(SourcePath ?? "windows");
        var chosen = _dialogs.PickSaveIso(Path.GetDirectoryName(SourcePath), $"{name}-{SelectedProfile?.Id ?? "tiny"}.iso");
        if (chosen is not null)
        {
            OutputPath = chosen;
        }
    }

    private async Task InspectSourceAsync(string path)
    {
        var previous = _inspect;
        var cts = _inspect = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        Editions.Clear();
        SelectedEdition = null;
        SourceMessage = _localizer.Get("Plan.Waiting");
        try
        {
            var inspection = await _planner.InspectAsync(path, cts.Token);
            var editions = inspection.Windows?.InstallImages.SelectMany(image => image.Metadata.Editions).ToList() ?? [];
            if (editions.Count == 0)
            {
                SourceMessage = _localizer.Get("Tiny.NoWindows");
                return;
            }

            foreach (var edition in editions)
            {
                Editions.Add(new TinyEditionOption(edition.Index, TinyView.EditionLabel(edition, _localizer)));
            }

            // Pro is what most people want; with only one edition there is no choice to make.
            SelectedEdition = Editions.FirstOrDefault(e => e.Label.Contains("Pro", StringComparison.OrdinalIgnoreCase) && !e.Label.Contains(" N", StringComparison.Ordinal))
                ?? Editions[0];
            SourceMessage = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The ISO could not be inspected");
            var description = ErrorCatalog.Describe(ex, _localizer);
            SourceMessage = $"{description.Cause} {description.Action}";
        }
    }

    private bool CanBuild() =>
        IsIdle
        && HasSource
        && HasOutput
        && SelectedEdition is not null
        && SelectedProfile is not null
        && (!NeedsAcknowledgement || AcknowledgeNoServicing)
        && !string.Equals(SourcePath, OutputPath, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        HasResult = false;
        IsBusy = true;
        Percent = 0;
        ProgressTitle = "";
        SpeedText = RemainingText = "";
        CancelText = _localizer.Get("Write.Cancel");

        using var soft = new CancellationTokenSource();
        using var abort = new CancellationTokenSource();
        _soft = soft;
        _abort = abort;

        try
        {
            var request = TinyRequestBuilder.Create(new TinySelection
            {
                SourcePath = SourcePath!,
                OutputPath = OutputPath!,
                ProfileId = SelectedProfile!.Id,
                EditionIndex = SelectedEdition!.Index,
                Groups = Groups.ToDictionary(g => g.Id, g => g.Applied),
                VolumeLabel = VolumeLabel,
                SkipHardwareChecks = SkipHardwareChecks,
                ForFat32 = ForFat32,
                LocalAccountName = LocalAccount,
                AcknowledgeNoServicing = AcknowledgeNoServicing,
            });

            var result = await _engine.RunJobAsync(request, new Progress<ProgressReport>(OnProgress), soft.Token, abort.Token);
            ShowOutcome(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Tiny build failed before it could report a result");
            ShowError(ex);
        }
        finally
        {
            _soft = _abort = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_soft is null || _abort is null)
        {
            return;
        }

        if (_soft.IsCancellationRequested)
        {
            _abort.Cancel();
            return;
        }

        _soft.Cancel();
        CancelText = _localizer.Get("Write.CancelNow");
    }

    private void BuildProfiles()
    {
        Profiles.Clear();
        foreach (var id in TinyProfiles.BuiltInIds)
        {
            var profile = TinyProfiles.Load(id);
            Profiles.Add(new TinyProfileOption(
                id,
                TinyView.ProfileName(id, _localizer),
                TinyView.ProfileDescription(id, _localizer),
                profile.BreaksServicing,
                profile.WindowsFamily == "11"));
        }
    }

    private void BuildGroups()
    {
        Groups.Clear();
        if (SelectedProfile is null)
        {
            return;
        }

        foreach (var option in TinyView.Groups(TinyProfiles.Load(SelectedProfile.Id), _localizer))
        {
            Groups.Add(new TinyGroupItem(option));
        }
    }

    private void OnProgress(ProgressReport report)
    {
        var view = ProgressView.From(report, _localizer);
        Percent = view.Percent;
        ProgressTitle = view.Title;
        SpeedText = view.Speed;
        RemainingText = view.Remaining;
    }

    private void ShowOutcome(EngineJobResult result)
    {
        switch (result.Outcome)
        {
            case JobOutcome.Succeeded:
                ShowResult(InfoBarSeverity.Success, _localizer.Get("Tiny.Done", ByteSize.FormatDuration(result.Duration), OutputPath!), "");
                if (_settings.Current.PlaySoundWhenDone)
                {
                    SystemSounds.Asterisk.Play();
                }

                break;
            case JobOutcome.Canceled:
                ShowResult(InfoBarSeverity.Warning, _localizer.Get("Write.Canceled"), "");
                break;
            default:
                ShowError(result.ToException()!);
                break;
        }
    }

    private void ShowError(Exception exception)
    {
        var description = ErrorCatalog.Describe(exception, _localizer);
        ShowResult(InfoBarSeverity.Error, description.Cause, $"{description.Action}  ({description.Code})");
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        HasResult = true;
    }
}
