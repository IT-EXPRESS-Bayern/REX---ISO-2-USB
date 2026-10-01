// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bootrix.App.ViewModels;

public sealed partial class WorkshopViewModel : ObservableObject
{
    private readonly ITargetPcCollector _collector;
    private readonly Localizer _localizer;
    private readonly ILogger<WorkshopViewModel> _logger;
    private TargetPcInfo? _info;
    private TargetPcAssessment? _assessment;

    public WorkshopViewModel(ITargetPcCollector collector, Localizer localizer, ILogger<WorkshopViewModel> logger)
    {
        _collector = collector;
        _localizer = localizer;
        _logger = logger;
        localizer.CultureChanged += (_, _) => Render();
    }

    public ObservableCollection<SummaryLine> Hardware { get; } = [];

    public ObservableCollection<RequirementLine> Requirements { get; } = [];

    public ObservableCollection<SummaryLine> Recommendation { get; } = [];

    public ObservableCollection<SummaryWarning> Notes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _verdict = "";

    [ObservableProperty]
    private Windows11Verdict _verdictKind;

    [ObservableProperty]
    private string _errorText = "";

    public bool IsIdle => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            _info = await _collector.CollectAsync(CancellationToken.None);
            _assessment = TargetPcAdvisor.Evaluate(_info);
            Render();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Analysis of this PC failed");
            var description = ErrorCatalog.Describe(ex, _localizer);
            ErrorText = $"{description.Cause} {description.Action} ({description.Code})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Render()
    {
        if (_info is null || _assessment is null)
        {
            return;
        }

        var view = TargetPcView.From(_info, _assessment, _localizer);
        Replace(Hardware, view.Hardware);
        Replace(Requirements, view.Requirements);
        Replace(Recommendation, view.Recommendation);
        Replace(Notes, view.Notes);
        Verdict = view.Verdict;
        VerdictKind = view.VerdictKind;
        HasResult = true;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
