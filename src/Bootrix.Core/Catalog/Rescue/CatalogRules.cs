// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>Checks shared by the entry and variant readers.</summary>
internal static partial class CatalogRules
{
    /// <summary>
    /// The shape of an SPDX expression without parentheses. "proprietary" and "proprietary-freeware" fit it too, and
    /// not knowing the SPDX list means a new licence in the data needs no code change.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+-]*(?: (?:AND|OR|WITH) [A-Za-z0-9][A-Za-z0-9.+-]*)*$")]
    private static partial Regex LicensePattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex VariantIdPattern();

    public static BootrixException Invalid(string detail, Exception? inner = null) =>
        new(ErrorCode.CatalogUnavailable, "rescue catalog: " + detail, inner);

    public static bool IsLicense(string text) => LicensePattern().IsMatch(text);

    public static bool IsSlug(string text) => text.Length <= 48 && SlugPattern().IsMatch(text);

    public static bool IsVariantId(string text) => text.Length <= 64 && VariantIdPattern().IsMatch(text);

    public static string Required(string? value, string where)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{where} is missing");
        }

        return value.Trim();
    }

    /// <summary>
    /// Only https and no credentials in the address: a catalog entry is a promise about where a file comes from,
    /// and a plain http address would let anyone on the path substitute it.
    /// </summary>
    public static Uri WebAddress(string? value, string where)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid($"{where} must be an absolute https address, not '{value}'");
        }

        return uri;
    }
}
