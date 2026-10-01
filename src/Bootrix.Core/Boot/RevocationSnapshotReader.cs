// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Bootrix.Core.Boot;

internal sealed record SnapshotSource(string? Repository, string? Ref, string? Commit, string? File, string? Sha256, string? License);

internal sealed record SnapshotSbat(string? Level, Dictionary<string, int>? Components);

internal sealed record SnapshotImage(string? Hash, string? Machine, string? File, string? Company, string? Added, string? Description);

internal sealed record SnapshotRevokedCertificate(string? Subject, string? Sha1, string? Added, string? Description);

internal sealed record SnapshotSvn(string? Component, string? Guid, string? Version, string? Changed, string? Description);

internal sealed record SnapshotTrustedCertificate(string? Name, string? Sha1, string? Sha256, string? Der);

internal sealed record SnapshotFile(
    int SchemaVersion,
    string? Retrieved,
    List<SnapshotSource>? Sources,
    SnapshotSbat? Sbat,
    List<SnapshotImage>? Images,
    List<SnapshotRevokedCertificate>? RevokedCertificates,
    List<SnapshotSvn>? Svns,
    List<SnapshotTrustedCertificate>? TrustedCertificates);

/// <summary>
/// Reads the revocations.json format written by tools/update-revocations.sh. The DTOs have no defaults of their own,
/// so every field is checked after deserialization instead of trusting the nullable annotations.
/// </summary>
internal static class RevocationSnapshotReader
{
    public const int SupportedSchemaVersion = 1;
    private const string ResourceName = "Bootrix.Core.Boot.revocations.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RevocationData ReadEmbedded()
    {
        using var stream = typeof(RevocationSnapshotReader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw RevocationData.Invalid("embedded revocation snapshot is missing");
        return Read(stream);
    }

    public static RevocationData Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SnapshotFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SnapshotFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw RevocationData.Invalid(ex.Message, ex);
        }

        if (file is null)
        {
            throw RevocationData.Invalid("empty document");
        }

        if (file.SchemaVersion != SupportedSchemaVersion)
        {
            throw RevocationData.Invalid("unsupported schema version " + file.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            return Map(file);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw RevocationData.Invalid(ex.Message, ex);
        }
    }

    private static RevocationData Map(SnapshotFile file)
    {
        var images = (file.Images ?? []).Select(i => new RevokedImage(
            Hex(i.Hash, 64, "hash").ToUpperInvariant(),
            ParseMachine(i.Machine),
            i.File ?? string.Empty,
            i.Company ?? string.Empty,
            ParseDate(i.Added),
            i.Description));

        var certificates = (file.RevokedCertificates ?? []).Select(c =>
            new RevokedCertificate(Need(c.Subject, "subject"), Hex(c.Sha1, 40, "sha1").ToLowerInvariant(), ParseDate(c.Added), c.Description));

        var svns = (file.Svns ?? []).Select(s =>
        {
            if (!SecurityVersion.TryParse(s.Version, out var version))
            {
                throw new FormatException("bad SVN version " + s.Version);
            }

            return new SvnRequirement(Guid.Parse(Need(s.Guid, "guid"), CultureInfo.InvariantCulture), s.Component ?? string.Empty, version, ParseDate(s.Changed), s.Description);
        });

        var sources = (file.Sources ?? []).Select(s => new RevocationSource(
            Need(s.Repository, "repository"), s.Ref ?? string.Empty, Need(s.Commit, "commit"), Need(s.File, "file"), s.Sha256 ?? string.Empty, s.License));

        SbatLevel? sbat = file.Sbat is null ? null : new SbatLevel(Need(file.Sbat.Level, "sbat level"), file.Sbat.Components ?? []);
        var trusted = (file.TrustedCertificates ?? []).Select(c => Convert.FromBase64String(Need(c.Der, "der")));

        // The constructor enumerates the queries, so format errors surface inside the caller's try block.
        return new RevocationData(ParseDate(file.Retrieved), sources.ToList(), images, certificates, [], svns, sbat, trusted.ToList());
    }

    private static string Need(string? value, string field) =>
        string.IsNullOrEmpty(value) ? throw new FormatException("missing field " + field) : value;

    private static string Hex(string? value, int length, string field)
    {
        if (value is null || value.Length != length || !value.All(Uri.IsHexDigit))
        {
            throw new FormatException("expected " + length.ToString(CultureInfo.InvariantCulture) + " hex digits in " + field);
        }

        return value;
    }

    private static DateOnly? ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static EfiMachine ParseMachine(string? value) => value switch
    {
        "x64" => EfiMachine.X64,
        "ia32" => EfiMachine.X86,
        "aarch64" => EfiMachine.Arm64,
        "arm" => EfiMachine.Arm,
        _ => EfiMachine.Unknown,
    };
}
