// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.App.Services;
using Bootrix.App.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Bootrix.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(IServiceProvider services, IContentDialogService dialogs, ThemeSwitcher theme)
    {
        InitializeComponent();

        dialogs.SetDialogHost(DialogHost);
        Navigation.SetServiceProvider(services);
        theme.Attach(this);

        Loaded += (_, _) => Navigation.Navigate(typeof(WritePage));
    }
}
