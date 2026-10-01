// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>The architecture names <see cref="CatalogVariant.Architectures"/> uses, and how vendors spell them.</summary>
internal static class Architectures
{
    public const string X64 = "x64";
    public const string Arm64 = "arm64";
    public const string I386 = "i386";

    /// <summary>"amd64", "x86_64" and "64bit" are all the same machine to a vendor.</summary>
    public static string? FromVendorName(string name) => name.ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" or "64bit" or "64-bit" => X64,
        "arm64" or "aarch64" => Arm64,
        "i386" or "i586" or "i686" or "x86" or "32bit" => I386,
        _ => null,
    };

    /// <summary>
    /// The architecture a resolve call is about: the requested one if the variant offers it, the only one if it offers
    /// a single one, null if it is architecture neutral. Anything else is a caller mistake.
    /// </summary>
    public static string? Select(CatalogVariant variant, string? requested)
    {
        ArgumentNullException.ThrowIfNull(variant);

        if (variant.Architectures.Count == 0)
        {
            return null;
        }

        if (requested is null)
        {
            return variant.Architectures.Count == 1
                ? variant.Architectures[0]
                : throw new ArgumentException($"'{variant.Id}' comes in several architectures; one must be chosen.", nameof(requested));
        }

        return variant.Architectures.FirstOrDefault(a => a.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"'{variant.Id}' is not offered for '{requested}'.", nameof(requested));
    }
}
