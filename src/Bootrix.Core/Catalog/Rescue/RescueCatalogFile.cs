// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

// The shape of rescue-catalog.json. Everything is nullable on purpose: these types have no defaults of their own,
// and the reader checks each field after deserialization instead of trusting the nullable annotations of a file
// that may come from the network.

internal sealed record CatalogFile(int? SchemaVersion, long? Version, DateOnly? Issued, DateOnly? Expires, List<EntryFile>? Entries);

internal sealed record EntryFile(
    string? Id,
    string? Name,
    TextFile? Description,
    string? Category,
    string? License,
    string? Homepage,
    List<string>? IncludedTools,
    List<string>? Notices,
    List<string>? Hints,
    List<VariantFile>? Variants);

internal sealed record TextFile(string? De, string? En);

internal sealed record VariantFile(
    string? Id,
    string? Label,
    string? Version,
    string? Architecture,
    DateOnly? ReleaseDate,
    DateOnly? EndOfSupport,
    bool? Recommended,
    string? WriteMode,
    string? Bios,
    string? Uefi,
    string? SecureBoot,
    string? Packaging,
    string? FileName,
    long? Size,
    string? Sha256,
    string? HashSource,
    SignatureFile? Signature,
    List<SourceFile>? Sources,
    string? ManualUrl,
    DateOnly? VerifiedOn);

internal sealed record SourceFile(string? Url, int? Priority, string? Location);

internal sealed record SignatureFile(string? Kind, string? Url, string? SignedUrl, string? Fingerprint);
