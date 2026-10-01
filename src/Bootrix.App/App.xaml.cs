// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Bootrix.App.Services;
using Bootrix.App.ViewModels;
using Bootrix.App.Views;
using Bootrix.Core.Errors;
using Bootrix.Core.Hosting;
using Bootrix.Core.Localization;
using Bootrix.Core.Settings;
using Bootrix.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace Bootrix.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = BootrixPaths.Detect(AppContext.BaseDirectory, perUser: true);
        Directory.CreateDirectory(paths.DataDirectory);
        _services = Configure(paths);

        ApplyLanguage(_services.GetRequiredService<SettingsStore>().Current.Language);
        DispatcherUnhandledException += OnUnhandledException;

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    internal static void ApplyLanguage(string setting)
    {
        var culture = LanguageChoice.Resolve(setting, CultureInfo.CurrentUICulture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Localizer.Default.SetCulture(culture);
    }

    private static ServiceProvider Configure(BootrixPaths paths)
    {
        var services = new ServiceCollection();
        services.AddBootrixCore(paths);
        services.AddBootrixWindows();

        services.AddSingleton(provider => new SettingsStore(
            Path.Combine(paths.DataDirectory, "settings.json"),
            provider.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ThemeSwitcher>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<WriteViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<WritePage>();
        services.AddTransient<SettingsPage>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logger = _services?.GetService<ILogger<App>>();
        logger?.LogError(e.Exception, "Unhandled exception in the user interface");

        var description = ErrorCatalog.Describe(e.Exception);
        MessageBox.Show(
            $"{description.Cause}\n\n{description.Action}\n\n{description.Code}",
            Localizer.Default.Get("App.Name"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
