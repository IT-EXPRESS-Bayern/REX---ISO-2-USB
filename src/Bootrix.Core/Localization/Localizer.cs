// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Resources;

namespace Bootrix.Core.Localization;

/// <summary>
/// Looks up UI texts in the embedded resources. The culture can be switched at runtime;
/// a missing key falls back to the key itself so a gap shows up in the UI instead of crashing.
/// </summary>
public sealed class Localizer
{
    private static readonly ResourceManager Resources =
        new("Bootrix.Core.Resources.Strings", typeof(Localizer).Assembly);

    public static Localizer Default { get; } = new();

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentUICulture;

    public event EventHandler? CultureChanged;

    public void SetCulture(CultureInfo culture)
    {
        Culture = culture;
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key)
    {
        return Resources.GetString(key, Culture) ?? key;
    }

    public string Get(string key, params object?[] args)
    {
        var format = Get(key);
        return args.Length == 0 ? format : string.Format(Culture, format, args);
    }

    public bool Has(string key) => Resources.GetString(key, Culture) is not null;
}
