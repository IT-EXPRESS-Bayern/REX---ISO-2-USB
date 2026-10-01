// SPDX-License-Identifier: GPL-3.0-or-later
using System.Media;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Bootrix.App.ViewModels;

/// <summary>
/// Progress, cancellation and result of one running job, for any page that starts jobs. The first cancel asks
/// the job to stop at the next safe point, the second ends it immediately.
/// </summary>
public sealed partial class JobProgressViewModel(SettingsStore settings, Localizer localizer, ILogger<JobProgressViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _soft;
    private CancellationTokenSource? _abort;

    [ObservableProperty]
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
    private string _cancelText = localizer.Get("Write.Cancel");

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultMessage = "";

    [ObservableProperty]
    private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    public bool IsIdle => !IsBusy;

    /// <summary>Runs a job in the engine (the elevated broker for the window). Returns null when it failed before it could report a result.</summary>
    public async Task<EngineJobResult?> RunAsync(IEngine engine, EngineJobRequest request, Func<TimeSpan, string>? successTitle = null)
    {
        EngineJobResult? result = null;
        await RunCoreAsync(
            async (progress, soft, abort) =>
            {
                var done = await engine.RunJobAsync(request, progress, soft, abort);
                result = done;
                ShowOutcome(done.Outcome, done.Duration, () => done.ToException(), successTitle);
            },
            ex => result = null);
        return result;
    }

    /// <summary>Runs a job in this process (optical drives need no elevated rights).</summary>
    public async Task<JobResult?> RunAsync(JobRunner runner, IJob job, Func<TimeSpan, string>? successTitle = null)
    {
        JobResult? result = null;
        await RunCoreAsync(
            async (progress, soft, abort) =>
            {
                var done = await runner.RunAsync(job, new DelegateProgressSink(progress.Report), soft, abort);
                result = done;
                ShowOutcome(done.Outcome, done.Duration, () => done.Error, successTitle);
            },
            ex => result = null);
        return result;
    }

    public void ShowSuccess(string title, string message = "") => Show(InfoBarSeverity.Success, title, message);

    public void ShowWarning(string title, string message = "") => Show(InfoBarSeverity.Warning, title, message);

    public void ShowError(Exception exception)
    {
        var description = ErrorCatalog.Describe(exception, localizer);
        Show(InfoBarSeverity.Error, description.Cause, $"{description.Action}  ({description.Code})");
    }

    public void ClearResult() => HasResult = false;

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
        CancelText = localizer.Get("Write.CancelNow");
    }

    private async Task RunCoreAsync(Func<IProgress<ProgressReport>, CancellationToken, CancellationToken, Task> run, Action<Exception> failed)
    {
        HasResult = false;
        IsBusy = true;
        Percent = 0;
        ProgressTitle = "";
        SpeedText = RemainingText = "";
        CancelText = localizer.Get("Write.Cancel");

        using var soft = new CancellationTokenSource();
        using var abort = new CancellationTokenSource();
        _soft = soft;
        _abort = abort;

        try
        {
            // Created on the UI thread, so that reports from the job arrive there too.
            IProgress<ProgressReport> progress = new Progress<ProgressReport>(OnProgress);
            await run(progress, soft.Token, abort.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The job failed before it could report a result");
            failed(ex);
            ShowError(ex);
        }
        finally
        {
            _soft = _abort = null;
            IsBusy = false;
        }
    }

    private void ShowOutcome(JobOutcome outcome, TimeSpan duration, Func<Exception?> error, Func<TimeSpan, string>? successTitle)
    {
        switch (outcome)
        {
            case JobOutcome.Succeeded:
                ShowSuccess(successTitle?.Invoke(duration) ?? localizer.Get("Job.Done", ByteSize.FormatDuration(duration)));
                if (settings.Current.PlaySoundWhenDone)
                {
                    SystemSounds.Asterisk.Play();
                }

                break;
            case JobOutcome.Canceled:
                ShowWarning(localizer.Get("Write.Canceled"));
                break;
            default:
                ShowError(error() ?? new BootrixException(ErrorCode.Unknown, "the job failed"));
                break;
        }
    }

    private void OnProgress(ProgressReport report)
    {
        var view = ProgressView.From(report, localizer);
        Percent = view.Percent;
        ProgressTitle = view.Title;
        SpeedText = view.Speed;
        RemainingText = view.Remaining;
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        HasResult = true;
    }
}
