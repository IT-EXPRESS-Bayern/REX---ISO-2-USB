// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Bootrix.App.Services;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Text;
using Bootrix.Core.Tiny;
using Bootrix.Core.Writing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

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

    public TinyViewModel(
        IEngine engine,
        IDialogService dialogs,
        MediaPlanService planner,
        SettingsStore settings,
        Localizer localizer,
        JobProgressViewModel job,
        ILogger<TinyViewModel> logger)
    {
        _engine = engine;
        _dialogs = dialogs;
        _planner = planner;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;
        Job = job;

        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobProgressViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(IsIdle));
                BuildCommand.NotifyCanExecuteChanged();
                PickSourceCommand.NotifyCanExecuteChanged();
                PickOutputCommand.NotifyCanExecuteChanged();
            }
        };
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

    public JobProgressViewModel Job { get; }

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

    public void Dispose()
    {
        _inspect?.Cancel();
        _inspect?.Dispose();
    }

    public bool IsIdle => Job.IsIdle;

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

        var output = OutputPath!;
        await Job.RunAsync(_engine, request, duration => _localizer.Get("Tiny.Done", ByteSize.FormatDuration(duration), output));
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
}
