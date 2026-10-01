// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using static Bootrix.Core.Catalog.Rescue.CatalogRules;

namespace Bootrix.Core.Catalog.Rescue;

internal static class RescueVariantReader
{
    private const int PgpFingerprintLength = 40;
    private const int MaxFileNameLength = 128;

    private static readonly string[] Architectures = ["x64", "x86", "arm64"];

    public static RescueVariant Map(VariantFile variant, string entry)
    {
        var id = variant.Id ?? string.Empty;
        if (!IsVariantId(id))
        {
            throw Invalid($"{entry}: variant id '{variant.Id}' is not valid");
        }

        var where = $"{entry}, variant '{id}'";
        var sources = Sources(variant.Sources, where);
        var manual = variant.ManualUrl is null ? null : WebAddress(variant.ManualUrl, where + ": manualUrl");
        if ((sources.Count == 0) == (manual is null))
        {
            throw Invalid($"{where}: exactly one of sources and manualUrl is required");
        }

        if (!RescueNames.TryParse<RescueWriteMode>(variant.WriteMode, out var writeMode))
        {
            throw Invalid($"{where}: unknown write mode '{variant.WriteMode}'");
        }

        if (sources.Count > 0 && writeMode == RescueWriteMode.None)
        {
            throw Invalid($"{where}: a downloadable variant needs a write mode");
        }

        var (sha256, origin) = Digest(variant, where);
        if (sources.Count > 1 && sha256 is null)
        {
            // Segments are spread over all sources; without a digest nothing would notice one that serves a different file.
            throw Invalid($"{where}: several sources need a SHA-256");
        }

        if (variant.Size is <= 0)
        {
            throw Invalid($"{where}: size must be positive");
        }

        if (variant.Architecture is not null && !Architectures.Contains(variant.Architecture))
        {
            throw Invalid($"{where}: unknown architecture '{variant.Architecture}'");
        }

        if (variant.ReleaseDate is { } released && variant.EndOfSupport is { } ends && ends < released)
        {
            throw Invalid($"{where}: support ends before the release");
        }

        return new RescueVariant
        {
            Id = id,
            Label = NullIfBlank(variant.Label),
            Version = NullIfBlank(variant.Version),
            Architecture = variant.Architecture,
            ReleaseDate = variant.ReleaseDate,
            EndOfSupport = variant.EndOfSupport,
            Recommended = variant.Recommended ?? false,
            WriteMode = writeMode,
            Firmware = new RescueFirmware(Support(variant.Bios, "bios", where), Support(variant.Uefi, "uefi", where), Support(variant.SecureBoot, "secureBoot", where)),
            Packaging = Packaging(variant.Packaging, where),
            FileName = FileName(variant.FileName, where),
            Size = variant.Size,
            Sha256 = sha256,
            HashOrigin = origin,
            Signature = variant.Signature is null ? null : Signature(variant.Signature, where),
            Sources = sources,
            ManualUrl = manual,
            VerifiedOn = variant.VerifiedOn,
        };
    }

    private static List<MirrorSource> Sources(List<SourceFile>? files, string where)
    {
        var sources = new List<MirrorSource>();
        foreach (var file in files ?? [])
        {
            var url = WebAddress(file?.Url, where + ": source url");
            if (sources.Any(s => s.Url == url))
            {
                throw Invalid($"{where}: source {url} is listed twice");
            }

            var priority = file!.Priority ?? 1;
            if (priority < 1 || (file.Location is { } place && (place.Length != 2 || !place.All(char.IsAsciiLetter))))
            {
                throw Invalid($"{where}: source {url} has an invalid priority or location");
            }

            sources.Add(new MirrorSource(url, priority, file.Location?.ToUpperInvariant()));
        }

        return sources;
    }

    private static (string? Sha256, RescueHashOrigin? Origin) Digest(VariantFile variant, string where)
    {
        var hasOrigin = variant.HashSource is not null;
        if (variant.Sha256 is null)
        {
            return hasOrigin ? throw Invalid($"{where}: hashSource without sha256") : (null, null);
        }

        if (!FileHash.TryCreate(HashKind.Sha256, variant.Sha256, out var hash))
        {
            throw Invalid($"{where}: sha256 is not 64 hex digits");
        }

        if (!RescueNames.TryParse<RescueHashOrigin>(variant.HashSource, out var origin))
        {
            throw Invalid($"{where}: a sha256 needs hashSource 'vendor' or 'pinned'");
        }

        return (hash!.Hex, origin);
    }

    private static BootSupport Support(string? text, string field, string where)
    {
        if (text is null)
        {
            return BootSupport.Unknown;
        }

        return RescueNames.TryParse<BootSupport>(text, out var support) ? support : throw Invalid($"{where}: {field} must be yes, no or unknown");
    }

    private static RescuePackaging Packaging(string? text, string where)
    {
        if (text is null)
        {
            return RescuePackaging.None;
        }

        return RescueNames.TryParse<RescuePackaging>(text, out var packaging) ? packaging : throw Invalid($"{where}: unknown packaging '{text}'");
    }

    private static string? FileName(string? name, string where)
    {
        if (name is null)
        {
            return null;
        }

        // The name ends up in a library folder; anything that could address another directory is refused here already.
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameLength || name is "." or ".." || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
        {
            throw Invalid($"{where}: fileName '{name}' is not a plain file name");
        }

        return name;
    }

    private static RescueSignature Signature(SignatureFile file, string where)
    {
        if (!RescueNames.TryParse<RescueSignatureKind>(file.Kind, out var kind))
        {
            throw Invalid($"{where}: unknown signature kind '{file.Kind}'");
        }

        var url = WebAddress(file.Url, where + ": signature url");
        var signed = file.SignedUrl is null ? null : WebAddress(file.SignedUrl, where + ": signature signedUrl");
        if (kind == RescueSignatureKind.OpenPgpChecksums && signed is null)
        {
            throw Invalid($"{where}: a signature over a checksum file must name the file");
        }

        var fingerprint = (file.Fingerprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (fingerprint.Length != PgpFingerprintLength || !fingerprint.All(char.IsAsciiHexDigit))
        {
            throw Invalid($"{where}: the signature key fingerprint is not 40 hex digits");
        }

        return new RescueSignature(kind, url, signed, fingerprint);
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
