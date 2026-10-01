// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Bootrix.Core.Localization;
using Wpf.Ui.Controls;

namespace Bootrix.App.Dialogs;

public partial class EraseConfirmDialog : ContentDialog
{
    private readonly IReadOnlyList<ConfirmRow> _rows;

    public EraseConfirmDialog(IReadOnlyList<ConfirmRow> rows, Localizer localizer)
    {
        _rows = rows;
        InitializeComponent();

        Rows.ItemsSource = rows;
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        Title = rows.Count == 1
            ? localizer.Get("Write.Confirm.Title")
            : localizer.Get("Write.Confirm.Title") + $" ({rows.Count})";
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfirmRow.Matches))
        {
            IsPrimaryButtonEnabled = _rows.All(r => r.Matches);
        }
    }
}
