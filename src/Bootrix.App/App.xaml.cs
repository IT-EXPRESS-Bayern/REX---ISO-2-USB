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

    /// <summary>
    /// Starts the window, visits every page and quits with exit code 0, or 1 and a file next to the program
    /// that says what broke. CI runs this on Windows because XAML mistakes only show up when the pages are loaded.
    /// </summary>
    internal bool SmokeTest { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = BootrixPaths.Detect(AppContext.BaseDirectory, perUser: true);
        Directory.CreateDirectory(paths.DataDirectory);
        _services = Configure(paths);

        ApplyLanguage(_services.GetRequiredService<SettingsStore>().Current.Language);
        DispatcherUnhandledException += OnUnhandledException;

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();

        if (SmokeTest)
        {
            _ = Dispatcher.InvokeAsync(() => RunSmokeTestAsync(window), DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task RunSmokeTestAsync(MainWindow window)
    {
        try
        {
            await Task.Delay(1500);
            window.Navigation.Navigate(typeof(DownloadsPage));
            await Task.Delay(1500);
            window.Navigation.Navigate(typeof(TinyPage));
            await Task.Delay(1000);
            window.Navigation.Navigate(typeof(WorkshopPage));
            await Task.Delay(500);
            window.Navigation.Navigate(typeof(SettingsPage));
            await Task.Delay(500);
            window.Navigation.Navigate(typeof(WritePage));
            await Task.Delay(1500);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            ReportSmokeFailure(ex);
        }
    }

    private void ReportSmokeFailure(Exception exception)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-error.txt"), exception.ToString());
        Shutdown(1);
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
        services.AddBootrixCatalog(paths);
        services.AddBootrixWindows();

        services.AddSingleton(provider => new SettingsStore(
            Path.Combine(paths.DataDirectory, "settings.json"),
            provider.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ThemeSwitcher>();
        services.AddSingleton<PageNavigator>();

        services.AddSingleton<MainWindow>();
        services.AddSingleton<WriteOptionsViewModel>();
        services.AddSingleton<WriteViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<TinyViewModel>();
        services.AddSingleton<WorkshopViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<WritePage>();
        services.AddTransient<DownloadsPage>();
        services.AddTransient<TinyPage>();
        services.AddTransient<WorkshopPage>();
        services.AddTransient<SettingsPage>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (SmokeTest)
        {
            e.Handled = true;
            ReportSmokeFailure(e.Exception);
            return;
        }

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
