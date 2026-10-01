// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Bootrix.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Bootrix.App.Services;

/// <summary>Applies the chosen theme; "like Windows" keeps following the system setting while the window is open.</summary>
public sealed class ThemeSwitcher(SettingsStore settings)
{
    private Window? _window;

    public void Attach(Window window)
    {
        _window = window;
        Apply(settings.Current.Theme);
        settings.Changed += (_, updated) => window.Dispatcher.Invoke(() => Apply(updated.Theme));
    }

    private void Apply(AppTheme theme)
    {
        if (_window is null)
        {
            return;
        }

        if (theme == AppTheme.System)
        {
            ApplicationThemeManager.Apply(SystemIsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
            SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, true);
            return;
        }

        SystemThemeWatcher.UnWatch(_window);
        ApplicationThemeManager.Apply(theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
    }

    private static bool SystemIsDark() => ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.HCBlack;
}
