// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Bootrix.Core.Localization;

namespace Bootrix.App.Localization;

/// <summary>
/// Binding source for <see cref="LocExtension"/>. Raising a change for the indexer makes every bound text
/// look itself up again, which is how the language switches while the window is open.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private LocalizationSource()
    {
        Localizer.Default.CultureChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Localizer.Default.Get(key);
}
