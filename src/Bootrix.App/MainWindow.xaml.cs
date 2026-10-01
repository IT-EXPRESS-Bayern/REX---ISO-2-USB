// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.App.Services;
using Bootrix.App.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Bootrix.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(IServiceProvider services, IContentDialogService dialogs, ThemeSwitcher theme, PageNavigator navigator, TaskbarProgress taskbar)
    {
        InitializeComponent();

        taskbar.Attach(Taskbar);
        dialogs.SetDialogHost(DialogHost);
        Navigation.SetServiceProvider(services);
        theme.Attach(this);
        navigator.Attach(page => Navigation.Navigate(page));

        Loaded += (_, _) => Navigation.Navigate(typeof(WritePage));
    }
}
