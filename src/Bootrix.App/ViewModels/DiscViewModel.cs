// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Windows;
using Bootrix.App.Services;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Bootrix.App.ViewModels;

/// <summary>
/// Burning, reading and erasing discs. The optical jobs talk to IMAPI and the drive's own device handle, which
/// standard users may use, so they run in this process and the page never needs the elevated broker.
/// </summary>
public sealed partial class DiscViewModel : ObservableObject
{
    private readonly IOpticalService _optical;
    private readonly BurnImageJob _burn;
    private readonly RipDiscJob _rip;
    private readonly EraseDiscJob _erase;
    private readonly JobRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly ILogger<DiscViewModel> _logger;
    private CancellationTokenSource? _soft;
    private CancellationTokenSource? _abort;

    public DiscViewModel(
        IOpticalService optical,
        BurnImageJob burn,
        RipDiscJob rip,
        EraseDiscJob erase,
        JobRunner runner,
        IDialogService dialogs,
        SettingsStore settings,
        Localizer localizer,
        ILogger<DiscViewModel> logger)
    {
        _optical = optical;
        _burn = burn;
        _rip = rip;
        _erase = erase;
        _runner = runner;
        _dialogs = dialogs;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;

        _cancelText = localizer.Get("Write.Cancel");
        BuildOptions();
        _selectedAction = Actions[0];
        _selectedVerify = VerifyLevels[1];
        _selectedErase = EraseModes[0];
        localizer.CultureChanged += (_, _) => BuildOptions();
        optical.DrivesChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync());
    }

    public ObservableCollection<DriveItem> Drives { get; } = [];

    public IReadOnlyList<DiscActionOption> Actions { get; private set; } = [];

    public IReadOnlyList<DiscVerifyOption> VerifyLevels { get; private set; } = [];

    public IReadOnlyList<DiscEraseOption> EraseModes { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(IsBurn), nameof(IsRip), nameof(IsErase), nameof(StartText))]
    private DiscActionOption _selectedAction;

    [ObservableProperty]
    private DiscVerifyOption _selectedVerify;

    [ObservableProperty]
    private DiscEraseOption _selectedErase;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private string? _imagePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _outputPath;

    [ObservableProperty]
    private bool _readBack;

    [ObservableProperty]
    private bool _finalize = true;

    [ObservableProperty]
    private bool _overwrite;

    [ObservableProperty]
    private bool _ejectWhenDone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickOutputCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasDrives;

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

    public bool IsIdle => !IsBusy;

    public bool IsBurn => SelectedAction.Action == DiscAction.Burn;

    public bool IsRip => SelectedAction.Action == DiscAction.Rip;

    public bool IsErase => SelectedAction.Action == DiscAction.Erase;

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputPath);

    public string StartText => _localizer.Get("Disc.Start." + SelectedAction.Action);

    partial void OnSelectedActionChanged(DiscActionOption value)
    {
        // Only recorders take a burn or an erase; any drive can be read.
        foreach (var item in Drives)
        {
            item.IsEnabled = value.Action == DiscAction.Rip || item.Drive.CanRecord;
            if (!item.IsEnabled)
            {
                item.IsSelected = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var drives = await _optical.EnumerateDrivesAsync(CancellationToken.None);
            var kept = Drives.Where(d => d.IsSelected).Select(d => d.Drive.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var items = new List<DriveItem>();
            foreach (var drive in drives)
            {
                var media = await _optical.QueryMediaAsync(drive, CancellationToken.None);
                var item = new DriveItem(drive, media, DiscView.Describe(drive, media, _localizer))
                {
                    IsEnabled = SelectedAction.Action == DiscAction.Rip || drive.CanRecord,
                };
                item.IsSelected = item.IsEnabled && kept.Contains(drive.Id);
                item.PropertyChanged += OnItemChanged;
                items.Add(item);
            }

            foreach (var old in Drives)
            {
                old.PropertyChanged -= OnItemChanged;
            }

            Drives.Clear();
            foreach (var item in items)
            {
                Drives.Add(item);
            }

            HasDrives = Drives.Count > 0;

            // One recorder is the common case; selecting it saves a click.
            if (Drives.Count(d => d.IsEnabled) == 1 && !Drives.Any(d => d.IsSelected))
            {
                Drives.First(d => d.IsEnabled).IsSelected = true;
            }

            StartCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing the optical drives failed");
            ShowError(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void PickImage()
    {
        if (_dialogs.PickDiscImage(_settings.Current.LastImageDirectory) is { } path)
        {
            ImagePath = path;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void PickOutput()
    {
        var chosen = _dialogs.PickSaveIso(_settings.Current.LastImageDirectory, "disc.iso");
        if (chosen is not null)
        {
            OutputPath = chosen;
        }
    }

    private bool CanStart() =>
        IsIdle
        && Drives.Any(d => d.IsSelected)
        && SelectedAction.Action switch
        {
            DiscAction.Burn => HasImage,
            DiscAction.Rip => HasOutput,
            _ => true,
        };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        HasResult = false;
        var selected = Drives.Where(d => d.IsSelected).ToList();
        var action = SelectedAction.Action;

        if (action != DiscAction.Burn && selected.Count != 1)
        {
            ShowResult(InfoBarSeverity.Warning, _localizer.Get("Disc.NeedOne"), "");
            return;
        }

        if (action == DiscAction.Burn && !File.Exists(ImagePath))
        {
            ShowError(new BootrixException(ErrorCode.ImageUnreadable, ImagePath ?? "") { Arguments = [ImagePath ?? ""] });
            return;
        }

        if (!await ConfirmIfDestructiveAsync(action, selected))
        {
            return;
        }

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
            var job = CreateJob(action, selected);
            IProgress<ProgressReport> progress = new Progress<ProgressReport>(OnProgress);
            var result = await _runner.RunAsync(job, new DelegateProgressSink(progress.Report), soft.Token, abort.Token);
            ShowOutcome(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The disc job failed before it could report a result");
            ShowError(ex);
        }
        finally
        {
            _soft = _abort = null;
            IsBusy = false;
            await RefreshAsync();
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

    [RelayCommand]
    private async Task EjectAsync(DriveItem? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        try
        {
            await _optical.EjectAsync(item.Drive);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ejecting failed");
            ShowError(ex);
        }
    }

    private IJob CreateJob(DiscAction action, List<DriveItem> selected) => action switch
    {
        DiscAction.Burn => _burn.Create(new BurnImageRequest
        {
            Drives = [.. selected.Select(d => d.Drive)],
            Image = DiscImageDetector.Open(ImagePath!),
            Options = new BurnOptions
            {
                Verify = SelectedVerify.Level,
                ReadBackSha256 = ReadBack,
                Finalize = Finalize,
                ForceOverwrite = Overwrite,
                EjectWhenDone = EjectWhenDone,
            },
        }),
        DiscAction.Rip => _rip.Create(new RipDiscRequest
        {
            Drive = selected[0].Drive,
            IsoPath = OutputPath!,
            Options = new RipOptions(),
        }),
        _ => _erase.Create(new EraseDiscRequest { Drive = selected[0].Drive, Mode = SelectedErase.Mode, EjectWhenDone = EjectWhenDone }),
    };

    private async Task<bool> ConfirmIfDestructiveAsync(DiscAction action, List<DriveItem> selected)
    {
        if (action == DiscAction.Erase)
        {
            return await _dialogs.ConfirmAsync(
                _localizer.Get("Disc.Confirm.Title"),
                _localizer.Get("Disc.Confirm.Erase", selected[0].Title),
                _localizer.Get("Disc.Confirm.Button"));
        }

        if (action == DiscAction.Burn && Overwrite)
        {
            var withData = selected.FirstOrDefault(d => d.Media.Condition == OpticalMediaCondition.Rewritable);
            if (withData is not null)
            {
                return await _dialogs.ConfirmAsync(
                    _localizer.Get("Disc.Confirm.Title"),
                    _localizer.Get("Disc.Confirm.Overwrite", withData.Title),
                    _localizer.Get("Disc.Confirm.Button"));
            }
        }

        return true;
    }

    private void BuildOptions()
    {
        Actions =
        [
            new DiscActionOption(DiscAction.Burn, _localizer.Get("Disc.Action.Burn")),
            new DiscActionOption(DiscAction.Rip, _localizer.Get("Disc.Action.Rip")),
            new DiscActionOption(DiscAction.Erase, _localizer.Get("Disc.Action.Erase")),
        ];
        VerifyLevels =
        [
            new DiscVerifyOption(BurnVerifyLevel.None, _localizer.Get("Disc.Verify.None")),
            new DiscVerifyOption(BurnVerifyLevel.Quick, _localizer.Get("Disc.Verify.Quick")),
            new DiscVerifyOption(BurnVerifyLevel.Full, _localizer.Get("Disc.Verify.Full")),
        ];
        EraseModes =
        [
            new DiscEraseOption(EraseMode.Quick, _localizer.Get("Disc.Erase.Quick")),
            new DiscEraseOption(EraseMode.Full, _localizer.Get("Disc.Erase.Full")),
        ];

        // The selections point at option objects that were just replaced.
        if (SelectedAction is not null)
        {
            SelectedAction = Actions.First(a => a.Action == SelectedAction.Action);
            SelectedVerify = VerifyLevels.First(v => v.Level == SelectedVerify.Level);
            SelectedErase = EraseModes.First(e => e.Mode == SelectedErase.Mode);
            OnPropertyChanged(nameof(Actions));
            OnPropertyChanged(nameof(VerifyLevels));
            OnPropertyChanged(nameof(EraseModes));
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriveItem.IsSelected))
        {
            StartCommand.NotifyCanExecuteChanged();
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

    private void ShowOutcome(JobResult result)
    {
        switch (result.Outcome)
        {
            case JobOutcome.Succeeded:
                ShowResult(InfoBarSeverity.Success, _localizer.Get("Disc.Done", ByteSize.FormatDuration(result.Duration)), "");
                if (_settings.Current.PlaySoundWhenDone)
                {
                    SystemSounds.Asterisk.Play();
                }

                break;
            case JobOutcome.Canceled:
                ShowResult(InfoBarSeverity.Warning, _localizer.Get("Write.Canceled"), "");
                break;
            default:
                ShowError(result.Error ?? new BootrixException(ErrorCode.Unknown, "the job failed"));
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
