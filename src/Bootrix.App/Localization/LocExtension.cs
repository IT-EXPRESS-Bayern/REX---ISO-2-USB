// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Data;
using System.Windows.Markup;

namespace Bootrix.App.Localization;

/// <summary>XAML access to the localized texts: <c>Text="{loc:Loc Write.Title}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
