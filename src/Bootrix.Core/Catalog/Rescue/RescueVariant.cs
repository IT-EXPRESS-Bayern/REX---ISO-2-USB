// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>What a digest in the catalog stands for, which decides how far it can be trusted.</summary>
public enum RescueHashOrigin
{
    /// <summary>The vendor publishes this SHA-256 itself.</summary>
    Vendor,

    /// <summary>The vendor publishes no SHA-256 (or only MD5); the catalog maintainers computed it from the vendor's file on <see cref="RescueVariant.VerifiedOn"/>.</summary>
    Pinned,
}

public enum RescuePackaging
{
    None,

    /// <summary>The download is a ZIP archive around the image; size and digest describe the archive as downloaded.</summary>
    Zip,
}

public enum RescueSignatureKind
{
    /// <summary>A detached OpenPGP signature over the image itself.</summary>
    OpenPgpDetached,

    /// <summary>The vendor signs its checksum file (<see cref="RescueSignature.SignedUrl"/>), not the image.</summary>
    OpenPgpChecksums,
}

/// <summary>Where the vendor's own signature lives and which key made it. Informational; the catalog's SHA-256 is what the download is checked against.</summary>
public sealed record RescueSignature(RescueSignatureKind Kind, Uri Url, Uri? SignedUrl, string Fingerprint);

/// <summary>One downloadable file of an entry: a release, an architecture or a form of packaging.</summary>
public sealed record RescueVariant
{
    public required string Id { get; init; }

    /// <summary>Short technical name that tells variants of one release apart, such as "USB image".</summary>
    public string? Label { get; init; }

    public string? Version { get; init; }

    /// <summary>"x64", "x86" or "arm64"; null when the image does not depend on it.</summary>
    public string? Architecture { get; init; }

    public DateOnly? ReleaseDate { get; init; }

    public DateOnly? EndOfSupport { get; init; }

    public bool Recommended { get; init; }

    public RescueWriteMode WriteMode { get; init; }

    public RescueFirmware Firmware { get; init; }

    public RescuePackaging Packaging { get; init; }

    /// <summary>Name of the file as the vendor serves it, used when the download is stored.</summary>
    public string? FileName { get; init; }

    public long? Size { get; init; }

    /// <summary>Lower-case hex; null when the file changes in place and no fixed digest can exist.</summary>
    public string? Sha256 { get; init; }

    public RescueHashOrigin? HashOrigin { get; init; }

    public RescueSignature? Signature { get; init; }

    /// <summary>Mirrors of the same file; empty for manual variants.</summary>
    public IReadOnlyList<MirrorSource> Sources { get; init; } = [];

    /// <summary>Set instead of <see cref="Sources"/> when the vendor offers no stable direct link; the user fetches the file there.</summary>
    public Uri? ManualUrl { get; init; }

    /// <summary>The day the addresses and digests were last checked against the vendor's site.</summary>
    public DateOnly? VerifiedOn { get; init; }
}
