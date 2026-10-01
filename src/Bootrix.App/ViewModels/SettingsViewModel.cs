// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;
using Bootrix.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bootrix.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly Localizer _localizer;
    private bool _loading;

    public SettingsViewModel(SettingsStore store, Localizer localizer)
    {
        _store = store;
        _localizer = localizer;

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
