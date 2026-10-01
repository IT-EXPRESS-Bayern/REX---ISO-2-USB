// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Bootrix.App.ViewModels;

namespace Bootrix.App.Views;

public partial class ToolsPage : Page
{
    private readonly ToolsViewModel _viewModel;

    public ToolsPage(ToolsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Loaded += async (_, _) => await viewModel.Devices.RefreshAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.Job.IsIdle && _viewModel.IsVerify && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_viewModel.Job.IsIdle && _viewModel.IsVerify && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _viewModel.SetImage(files[0]);
        }
    }
}
