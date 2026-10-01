// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;
using Bootrix.Core.Settings;
using System.Diagnostics;
using Bootrix.App.Services;
using Bootrix.Core;
using Bootrix.Core.Diagnostics;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bootrix.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly Localizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IEngine _engine;
    private readonly BootrixPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public SettingsViewModel(SettingsStore store, Localizer localizer, IDialogService dialogs, IEngine engine, BootrixPaths paths, ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _localizer = localizer;
        _dialogs = dialogs;
        _engine = engine;
        _paths = paths;
        _logger = logger;

        Load(store.Current);
        localizer.CultureChanged += (_, _) => BuildOptions();
    }

    [ObservableProperty]
    private IReadOnlyList<LanguageOption> _languages = [];

    [ObservableProperty]
    private IReadOnlyList<ThemeOption> _themes = [];

    [ObservableProperty]
    private string _language = "";

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private bool _showUsbHardDisks;

    [ObservableProperty]
    private bool _serviceMode;

    [ObservableProperty]
    private bool _verifyAfterWrite;

    [ObservableProperty]
    private bool _playSoundWhenDone;

    [ObservableProperty]
    private string _teamProfileDirectory = "";

    public bool HasTeamProfileDirectory => TeamProfileDirectory.Length > 0;

    partial void OnTeamProfileDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(HasTeamProfileDirectory));
        Save(s => s with { TeamProfileDirectory = value.Length == 0 ? null : value });
    }

    [RelayCommand]
    private void PickTeamProfileDirectory()
    {
        if (_dialogs.PickFolder(string.IsNullOrEmpty(TeamProfileDirectory) ? null : TeamProfileDirectory) is { } folder)
        {
            TeamProfileDirectory = folder;
        }
    }

    [RelayCommand]
    private void ClearTeamProfileDirectory() => TeamProfileDirectory = "";

    public string AboutText => _localizer.Get("Settings.About.Text", AppInfo.Version);

    [ObservableProperty]
    private string _diagnosticsStatus = "";

    public bool HasDiagnosticsStatus => DiagnosticsStatus.Length > 0;

    partial void OnDiagnosticsStatusChanged(string value) => OnPropertyChanged(nameof(HasDiagnosticsStatus));

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.LogDirectory}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenSourcePage() => _dialogs.OpenUrl("https://github.com/IT-EXPRESS-Bayern/REX---ISO-2-USB");

    [RelayCommand]
    private async Task CreateDiagnosticsAsync()
    {
        var target = _dialogs.PickSaveZip($"bootrix-diagnose-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (target is null)
        {
            return;
        }

        string? brokerArchive = null;
        if (await _dialogs.ConfirmAsync(_localizer.Get("Diagnose.Confirm.Title"), _localizer.Get("Diagnose.Confirm.Text"), _localizer.Get("Diagnose.Confirm.Button")))
        {
            var temporary = Path.Combine(Path.GetTempPath(), "bootrix-broker-logs-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
            try
            {
                var result = await _engine.RunJobAsync(new CollectLogsJobRequest { OutputPath = temporary }, new Progress<ProgressReport>(_ => { }), CancellationToken.None);
                brokerArchive = result.Succeeded && File.Exists(temporary) ? temporary : null;
            }
            catch (Exception ex)
            {
                // Declining the administrator prompt is allowed; the package is made without those logs.
                _logger.LogInformation(ex, "The logs of the administrator process were not collected");
            }
        }

        try
        {
            await using var output = File.Create(target);
            await using var broker = brokerArchive is null ? null : File.OpenRead(brokerArchive);
            DiagnosticsPackage.Write(
                output,
                [new DiagnosticsSource(_paths.LogDirectory, "*.log", "app-logs")],
                SystemReport.Describe(elevated: false),
                broker is null ? null : [("broker", broker)]);
            DiagnosticsStatus = _localizer.Get(brokerArchive is null ? "Diagnose.DoneWithoutBroker" : "Diagnose.Done", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The diagnostics package could not be written");
            var description = ErrorCatalog.Describe(new BootrixException(ErrorCode.FileCopyFailed, ex.Message, ex) { Arguments = [target, ex.Message] }, _localizer);
            DiagnosticsStatus = $"{description.Cause} {description.Action}";
        }
        finally
        {
            if (brokerArchive is not null)
            {
                File.Delete(brokerArchive);
            }
        }
    }

    partial void OnLanguageChanged(string value)
    {
        if (Save(s => s with { Language = value }))
        {
            App.ApplyLanguage(value);
        }
    }

    partial void OnThemeChanged(AppTheme value) => Save(s => s with { Theme = value });

    partial void OnShowUsbHardDisksChanged(bool value) => Save(s => s with { ShowUsbHardDisks = value });

    partial void OnServiceModeChanged(bool value) => Save(s => s with { ServiceMode = value });

    partial void OnVerifyAfterWriteChanged(bool value) => Save(s => s with { VerifyAfterWrite = value });

    partial void OnPlaySoundWhenDoneChanged(bool value) => Save(s => s with { PlaySoundWhenDone = value });

    private void Load(AppSettings settings)
    {
        _loading = true;
        BuildOptions();
        Language = settings.Language;
        Theme = settings.Theme;
        ShowUsbHardDisks = settings.ShowUsbHardDisks;
        ServiceMode = settings.ServiceMode;
        VerifyAfterWrite = settings.VerifyAfterWrite;
        PlaySoundWhenDone = settings.PlaySoundWhenDone;
        TeamProfileDirectory = settings.TeamProfileDirectory ?? "";
        _loading = false;
    }

    /// <summary>Rebuilds the lists so the entries that are translated follow a language switch.</summary>
    private void BuildOptions()
    {
        var selectedLanguage = Language;
        var selectedTheme = Theme;
        var wasLoading = _loading;
        _loading = true;

        Languages =
        [
            new LanguageOption("", _localizer.Get("Settings.Language.System")),
            new LanguageOption("de-DE", "Deutsch"),
            new LanguageOption("en-US", "English"),
        ];
        Themes =
        [
            new ThemeOption(AppTheme.System, _localizer.Get("Settings.Theme.System")),
            new ThemeOption(AppTheme.Light, _localizer.Get("Settings.Theme.Light")),
            new ThemeOption(AppTheme.Dark, _localizer.Get("Settings.Theme.Dark")),
        ];

        Language = selectedLanguage;
        Theme = selectedTheme;
        _loading = wasLoading;
    }

    private bool Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading)
        {
            return false;
        }

        _store.Update(change);
        return true;
    }
}
