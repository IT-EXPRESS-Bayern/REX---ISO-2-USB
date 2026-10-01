// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Bootrix.App.ViewModels;

namespace Bootrix.App.Views;

public partial class DiscPage : Page
{
    private readonly DiscViewModel _viewModel;

    public DiscPage(DiscViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.IsIdle && _viewModel.IsBurn && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_viewModel.IsIdle && _viewModel.IsBurn && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _viewModel.ImagePath = files[0];
        }
    }
}
