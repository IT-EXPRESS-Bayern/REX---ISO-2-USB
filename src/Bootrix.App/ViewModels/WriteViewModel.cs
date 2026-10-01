// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using Bootrix.App.Services;
using Bootrix.Core.Engine;
using Bootrix.Core.Images;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;
using Bootrix.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Windows;
using Wpf.Ui.Controls;

namespace Bootrix.App.ViewModels;

public sealed partial class WriteViewModel : ObservableObject, IDisposable
{
    private static readonly string[] RawExtensions = [".iso", ".img", ".bin", ".raw"];

    private readonly IEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly ILogger<WriteViewModel> _logger;
    private readonly MediaPlanService _planner;
    private CancellationTokenSource? _previewCts;
    private ImageInspection? _inspection;
    private string? _inspectedPath;

    private CancellationTokenSource? _soft;
    private CancellationTokenSource? _abort;
    private int _refreshRunning;

    public WriteViewModel(
        IEngine engine,
        IDialogService dialogs,
        SettingsStore settings,
        Localizer localizer,
        MediaPlanService planner,
        WriteOptionsViewModel options,
        ILogger<WriteViewModel> logger)
    {
        _planner = planner;
        Options = options;
        _engine = engine;
        _dialogs = dialogs;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;

        _verify = settings.Current.VerifyAfterWrite;
        _cancelText = localizer.Get("Write.Cancel");

        _engine.DevicesChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync());
        settings.Changed += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync());
        localizer.CultureChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync());

        options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WriteOptionsViewModel.Source))
            {
                StartCommand.NotifyCanExecuteChanged();
            }

            if (e.PropertyName != nameof(WriteOptionsViewModel.IsWindowsImage))
            {
                SchedulePreview();
            }
        };
        _previewMessage = localizer.Get("Plan.Waiting");
    }

    public WriteOptionsViewModel Options { get; }

    public void Dispose()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
    }

    public ObservableCollection<SummaryLine> PreviewLines { get; } = [];

    public ObservableCollection<SummaryWarning> PreviewWarnings { get; } = [];

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _imagePath;

    [ObservableProperty]
    private string _imageInfo = "";

    [ObservableProperty]
    private bool _verify;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
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

    [ObservableProperty]
    private bool _hasDevices;

    /// <summary>Why there is no preview (nothing chosen yet, or the image cannot be written), shown instead of the plan.</summary>
    [ObservableProperty]
    private string _previewMessage;

    [ObservableProperty]
    private bool _hasPreviewMessage = true;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _previewBlocksWriting;

    public bool IsIdle => !IsBusy;

    public int SelectedCount => Devices.Count(d => d.IsSelected);

    public string SelectedText => _localizer.Get("Write.Selected", SelectedCount);

    partial void OnImagePathChanged(string? value)
    {
        ImageInfo = DescribeImage(value);
        SchedulePreview();
    }

    partial void OnVerifyChanged(bool value) => SchedulePreview();

    public void SetImage(string path)
    {
        ImagePath = path;
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            _settings.Update(s => s with { LastImageDirectory = directory });
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Browse()
    {
        var path = _dialogs.PickImage(_settings.Current.LastImageDirectory);
        if (path is not null)
        {
            SetImage(path);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task RefreshAsync()
    {
        // The change notifications of the disk watcher can arrive in bursts; one refresh at a time is enough.
        if (IsBusy || Interlocked.Exchange(ref _refreshRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var settings = _settings.Current;
            var filter = new DiskFilter
            {
                IncludeUsbHardDisks = settings.ShowUsbHardDisks,
                IncludeInternalDisks = settings.ServiceMode,
                IncludeBlocked = true,
            };

            var devices = await _engine.ListDisksAsync(filter, CancellationToken.None);
            var kept = Devices.Where(d => d.IsSelected).Select(d => d.Device.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in Devices)
            {
                item.PropertyChanged -= OnItemChanged;
            }

            Devices.Clear();
            foreach (var device in devices)
            {
                var description = DeviceDescription.Describe(device, _localizer);
                var item = new DeviceItem(device, description)
                {
                    IsSelected = description.Selectable && kept.Contains(device.DevicePath),
                };
                item.PropertyChanged += OnItemChanged;
                Devices.Add(item);
            }

            HasDevices = Devices.Count > 0;
            OnSelectionChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing the disks failed");
            ShowError(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        HasResult = false;
        var targets = Devices.Where(d => d.IsSelected).ToList();
        var source = Options.Source;

        var problem = source == WriteSource.Image
            ? WriteChecks.FirstProblem(ImagePath, RawImageSize(ImagePath), [.. targets.Select(t => t.Device)], _localizer)
            : WriteChecks.FirstProblem("-", null, [.. targets.Select(t => t.Device)], _localizer);
        if (problem is not null)
        {
            ShowResult(InfoBarSeverity.Warning, problem, "");
            return;
        }

        if (source == WriteSource.Image && !File.Exists(ImagePath))
        {
            ShowError(new BootrixException(ErrorCode.ImageUnreadable, ImagePath) { Arguments = [ImagePath!] });
            return;
        }

        if (!await _dialogs.ConfirmEraseAsync([.. targets.Select(t => new EraseTarget(t.Device, t.Description))]))
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
            var identified = new List<EngineTarget>();
            foreach (var target in targets)
            {
                var identity = await _engine.CaptureIdentityAsync(target.Device.DevicePath, soft.Token);
                identified.Add(new EngineTarget(target.Device.DevicePath, identity));
            }

            var request = new WriteImageJobRequest
            {
                ImagePath = source == WriteSource.Image ? ImagePath : null,
                Source = source,
                Targets = identified,
                Spec = Options.ToSpec(Verify),
            };
            var result = await _engine.RunJobAsync(request, new Progress<ProgressReport>(OnProgress), soft.Token, abort.Token);
            ShowOutcome(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The write failed before it could report a result");
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

    private bool CanStart() => !IsBusy && (Options.Source != WriteSource.Image || !string.IsNullOrWhiteSpace(ImagePath)) && Devices.Any(d => d.IsSelected) && !PreviewBlocksWriting;

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceItem.IsSelected))
        {
            OnSelectionChanged();
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedText));
        StartCommand.NotifyCanExecuteChanged();
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        var previous = _previewCts;
        var cts = _previewCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        _ = RunPreviewAsync(cts.Token);
    }

    private async Task RunPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Typing in the label box or clicking through options should not inspect the image on every keystroke.
            await Task.Delay(250, cancellationToken);
            await RefreshPreviewAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The preview failed");
            ShowPreviewMessage(ErrorCatalog.Describe(ex, _localizer).Cause, blocks: false);
        }
    }

    private async Task RefreshPreviewAsync(CancellationToken cancellationToken)
    {
        var device = Devices.FirstOrDefault(d => d.IsSelected)?.Device;
        var source = Options.Source;
        if (device is null || (source == WriteSource.Image && (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))))
        {
            ShowPreviewMessage(_localizer.Get("Plan.Waiting"), blocks: false);
            return;
        }

        ImageInspection inspection;
        if (source == WriteSource.Image)
        {
            if (_inspection is null || !string.Equals(_inspectedPath, ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                _inspection = null;
                _inspection = await _planner.InspectAsync(ImagePath!, cancellationToken);
                _inspectedPath = ImagePath;
            }

            inspection = _inspection;
        }
        else
        {
            inspection = MediaPlanService.InspectionFor(source);
        }

        Options.IsWindowsImage = inspection.Profile.Kind is ImageKind.WindowsSetup or ImageKind.WindowsPe;

        try
        {
            var preview = MediaPlanService.Plan(inspection, Options.ToSpec(Verify).Target, device);
            var summary = PlanSummary.From(preview, _localizer, source);

            PreviewLines.Clear();
            foreach (var line in summary.Lines)
            {
                PreviewLines.Add(line);
            }

            PreviewWarnings.Clear();
            foreach (var warning in summary.Warnings)
            {
                PreviewWarnings.Add(warning);
            }

            HasPreview = true;
            HasPreviewMessage = false;
            PreviewBlocksWriting = summary.HasErrors;
        }
        catch (BootrixException ex)
        {
            // A plan that cannot be made (too small, unsupported combination) is an answer, not a crash.
            var description = ErrorCatalog.Describe(ex, _localizer);
            ShowPreviewMessage($"{description.Cause} {description.Action}", blocks: true);
        }
    }

    private void ShowPreviewMessage(string message, bool blocks)
    {
        PreviewLines.Clear();
        PreviewWarnings.Clear();
        HasPreview = false;
        PreviewMessage = message;
        HasPreviewMessage = true;
        PreviewBlocksWriting = blocks;
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
                ShowResult(
                    InfoBarSeverity.Success,
                    _localizer.Get("Write.Done", ByteSize.FormatDuration(result.Duration)),
                    result.ImageSha256 is { } hash ? _localizer.Get("Write.DoneHash", hash) : "");
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

    private string DescribeImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "";
        }

        return ByteSize.Format(new FileInfo(path).Length, _localizer.Culture);
    }

    /// <summary>Only plain images have a size that says what lands on the disk; compressed ones are checked while writing.</summary>
    private static long? RawImageSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return RawExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? new FileInfo(path).Length : null;
    }
}
