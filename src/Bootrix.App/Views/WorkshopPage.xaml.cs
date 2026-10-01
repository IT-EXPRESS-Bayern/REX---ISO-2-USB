// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Controls;
using Bootrix.App.ViewModels;

namespace Bootrix.App.Views;

public partial class WorkshopPage : Page
{
    public WorkshopPage(WorkshopViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
