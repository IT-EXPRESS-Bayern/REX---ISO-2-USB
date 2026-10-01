// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Controls;
using Bootrix.App.ViewModels;

namespace Bootrix.App.Views;

public partial class DownloadsPage : Page
{
    public DownloadsPage(DownloadsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        Loaded += async (_, _) => await viewModel.EnsureLoadedAsync();
    }
}
