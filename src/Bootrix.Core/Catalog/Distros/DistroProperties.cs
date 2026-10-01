// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Distros;

/// <summary>Keys and values the distribution providers put into <see cref="CatalogVariant.Properties"/> for callers that act on them.</summary>
public static class DistroProperties
{
    /// <summary>
    /// Where the expected digest comes from. <see cref="PinnedKey"/> ("signed"): a vendor checksum file whose OpenPGP
    /// signature is verified against a pinned key when the variant is resolved; resolving fails instead of falling
    /// back. <see cref="TlsOnly"/> ("unsigned"): the vendor publishes no signed digest, so it is only as trustworthy
    /// as its HTTPS site.
    /// </summary>
    public const string HashTrust = "hashTrust";

    public const string PinnedKey = "signed";

    public const string TlsOnly = "unsigned";

    /// <summary>The download is an archive around the image (value "zip"); <see cref="ArchiveEntry"/> names the image inside.</summary>
    public const string Archive = "archive";

    public const string ArchiveEntry = "archiveEntry";
}
