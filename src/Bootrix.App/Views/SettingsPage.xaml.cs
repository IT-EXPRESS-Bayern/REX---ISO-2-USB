// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Controls;
using Bootrix.App.ViewModels;

namespace Bootrix.App.Views;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
