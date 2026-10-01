// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.App.Services;
using Bootrix.Core.Engine;
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Text;
using Bootrix.Core.Storage.Testing;
using Bootrix.Core.Writing.Verify;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bootrix.App.ViewModels;

public enum Tool
{
    Verify,
    Restore,
    Test,
}

/// <summary>Checking a medium against its image and bringing a drive back to a clean state.</summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly IEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;

    public ToolsViewModel(
        IEngine engine,
        IDialogService dialogs,
        SettingsStore settings,
        Localizer localizer,
        DeviceListViewModel devices,
        JobProgressViewModel job)
    {
        _engine = engine;
        _dialogs = dialogs;
        _settings = settings;
        _localizer = localizer;
        Devices = devices;
        Job = job;

        BuildOptions();
        _selectedTool = Tools[0];
        _selectedMode = Modes[0];
        _selectedScheme = Schemes[0];
        _selectedFileSystem = FileSystems[0];
        _selectedTestMode = TestModes[0];
        localizer.CultureChanged += (_, _) => BuildOptions();
        devices.SelectionChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobProgressViewModel.IsBusy))
            {
                devices.IsLocked = job.IsBusy;
                StartCommand.NotifyCanExecuteChanged();
                PickImageCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public DeviceListViewModel Devices { get; }

    public JobProgressViewModel Job { get; }

    public IReadOnlyList<OptionItem<Tool>> Tools { get; private set; } = [];

    public IReadOnlyList<OptionItem<VerifyMode>> Modes { get; private set; } = [];

    public IReadOnlyList<OptionItem<PartitionScheme>> Schemes { get; private set; } = [];

    public IReadOnlyList<OptionItem<FileSystemKind>> FileSystems { get; private set; } = [];

    public IReadOnlyList<OptionItem<StickTestMode>> TestModes { get; private set; } = [];

    /// <summary>The findings of the last stick test, one entry per stick.</summary>
    public ObservableCollection<StickTestSummary> TestResults { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(IsVerify), nameof(IsRestore), nameof(IsTest), nameof(StartText), nameof(Hint))]
    private OptionItem<Tool> _selectedTool;

    [ObservableProperty]
    private OptionItem<VerifyMode> _selectedMode;

    [ObservableProperty]
    private OptionItem<PartitionScheme> _selectedScheme;

    [ObservableProperty]
    private OptionItem<FileSystemKind> _selectedFileSystem;

    [ObservableProperty]
    private OptionItem<StickTestMode> _selectedTestMode;

    [ObservableProperty]
    private bool _hasTestResults;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private string? _imagePath;

    [ObservableProperty]
    private string _label = "";

    public bool IsVerify => SelectedTool.Value == Tool.Verify;

    public bool IsRestore => SelectedTool.Value == Tool.Restore;

    public bool IsTest => SelectedTool.Value == Tool.Test;

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

    public string StartText => _localizer.Get("Tools.Start." + SelectedTool.Value);

    public string Hint => _localizer.Get("Tools." + SelectedTool.Value + ".Hint");

    public void SetImage(string path) => ImagePath = path;

    [RelayCommand(CanExecute = nameof(CanPickImage))]
    private void PickImage()
    {
        if (_dialogs.PickImage(_settings.Current.LastImageDirectory) is { } path)
        {
            ImagePath = path;
        }
    }

    private bool CanPickImage() => Job.IsIdle;

    private bool CanStart() =>
        Job.IsIdle
        && Devices.Selected.Count > 0
        && (SelectedTool.Value != Tool.Verify || HasImage);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Job.ClearResult();
        var targets = Devices.Selected;

        if ((IsRestore || IsTest) && !await _dialogs.ConfirmEraseAsync([.. targets.Select(t => new EraseTarget(t.Device, t.Description))]))
        {
            return;
        }

        // The fingerprint is taken at the moment of the decision, so a disk that was swapped in the meantime is refused.
        var identified = new List<EngineTarget>();
        try
        {
            foreach (var target in targets)
            {
                identified.Add(new EngineTarget(target.Device.DevicePath, await _engine.CaptureIdentityAsync(target.Device.DevicePath, CancellationToken.None)));
            }
        }
        catch (Exception ex)
        {
            Job.ShowError(ex);
            return;
        }

        if (IsVerify)
        {
            var request = new VerifyJobRequest { ImagePath = ImagePath!, Targets = identified, Mode = SelectedMode.Value };
            var result = await Job.RunAsync(_engine, request);
            if (result is { Succeeded: true })
            {
                Job.ShowSuccess(_localizer.Get("Tools.Verify.Done", ByteSize.Format(result.ImageBytes, _localizer.Culture)));
            }
        }
        else if (IsTest)
        {
            TestResults.Clear();
            HasTestResults = false;
            var result = await Job.RunAsync(_engine, new StickTestJobRequest { Targets = identified, Mode = SelectedTestMode.Value });
            if (result is { Succeeded: true, ReportJson: { } json })
            {
                foreach (var report in StickTestReport.Deserialize(json))
                {
                    TestResults.Add(StickTestView.Describe(report, _localizer));
                }

                HasTestResults = TestResults.Count > 0;
                Job.ShowWarning(_localizer.Get("Test.AfterwardsFormat"));
            }
        }
        else
        {
            var request = new RestoreDriveJobRequest
            {
                Targets = identified,
                Scheme = SelectedScheme.Value,
                FileSystem = SelectedFileSystem.Value,
                Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            };
            await Job.RunAsync(_engine, request);
        }

        await Devices.RefreshAsync();
    }

    private void BuildOptions()
    {
        Tools =
        [
            new(Tool.Verify, _localizer.Get("Tools.Tool.Verify")),
            new(Tool.Restore, _localizer.Get("Tools.Tool.Restore")),
            new(Tool.Test, _localizer.Get("Tools.Tool.Test")),
        ];
        TestModes =
        [
            new(StickTestMode.Capacity, _localizer.Get("Tools.Test.Mode.Capacity")),
            new(StickTestMode.Quick, _localizer.Get("Tools.Test.Mode.Quick")),
            new(StickTestMode.Thorough, _localizer.Get("Tools.Test.Mode.Thorough")),
        ];
        Modes =
        [
            new(VerifyMode.Auto, _localizer.Get("Tools.Verify.Mode.Auto")),
            new(VerifyMode.Raw, _localizer.Get("Tools.Verify.Mode.Raw")),
            new(VerifyMode.Files, _localizer.Get("Tools.Verify.Mode.Files")),
        ];
        Schemes =
        [
            new(PartitionScheme.Mbr, _localizer.Get("Opt.Scheme.Mbr")),
            new(PartitionScheme.Gpt, _localizer.Get("Opt.Scheme.Gpt")),
        ];
        FileSystems =
        [
            new(FileSystemKind.Auto, _localizer.Get("Opt.FileSystem.Auto")),
            new(FileSystemKind.Fat32, _localizer.Get("Opt.FileSystem.Fat32")),
            new(FileSystemKind.ExFat, _localizer.Get("Opt.FileSystem.ExFat")),
            new(FileSystemKind.Ntfs, _localizer.Get("Opt.FileSystem.Ntfs")),
        ];

        // The selections point at option objects that were just replaced.
        if (SelectedTool is not null)
        {
            SelectedTool = Tools.First(o => o.Value == SelectedTool.Value);
            SelectedMode = Modes.First(o => o.Value == SelectedMode.Value);
            SelectedScheme = Schemes.First(o => o.Value == SelectedScheme.Value);
            SelectedFileSystem = FileSystems.First(o => o.Value == SelectedFileSystem.Value);
            SelectedTestMode = TestModes.First(o => o.Value == SelectedTestMode.Value);
            OnPropertyChanged(nameof(TestModes));
            OnPropertyChanged(nameof(Tools));
            OnPropertyChanged(nameof(Modes));
            OnPropertyChanged(nameof(Schemes));
            OnPropertyChanged(nameof(FileSystems));
            OnPropertyChanged(nameof(StartText));
            OnPropertyChanged(nameof(Hint));
        }
    }
}
