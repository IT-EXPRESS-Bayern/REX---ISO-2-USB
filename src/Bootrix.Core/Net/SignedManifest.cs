// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Net;

/// <summary>A file the manifest vouches for: what it is called, where it lives, how big it is and its SHA-256.</summary>
public sealed record ManifestArtifact
{
    public required string Name { get; init; }

    public Uri? Url { get; init; }

    public required long Size { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>A download of this artifact that checks size and digest; needs <see cref="Url"/>.</summary>
    public DownloadRequest ToRequest(DownloadOptions? options = null)
    {
        if (Url is null)
        {
            throw new InvalidOperationException($"Artifact '{Name}' has no address.");
        }

        return new DownloadRequest(Url)
        {
            ExpectedSize = Size,
            ExpectedHashes = [new FileHash(HashKind.Sha256, Sha256)],
            Options = options ?? new DownloadOptions(),
        };
    }
}

/// <summary>
/// The content of a signed metadata document: catalog, revocation data or update information. Only instances
/// returned by <see cref="SignedManifestVerifier"/> have been checked; the type itself says nothing about trust.
/// </summary>
public sealed record SignedManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    /// <summary>Which kind of document this is, such as "catalog". The rollback counter is kept per channel.</summary>
    public required string Channel { get; init; }

    /// <summary>Increases with every publication; a lower value than one already seen is a rollback.</summary>
    public required long Version { get; init; }

    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>After this moment the document is no longer accepted, so a server cannot keep serving stale data.</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }

    public JsonElement Payload { get; init; }

    public IReadOnlyList<ManifestArtifact> Artifacts { get; init; } = [];

    /// <summary>Set by the verifier: the key that signed it.</summary>
    [JsonIgnore]
    public string? SignedByKeyId { get; init; }

    public ManifestArtifact? FindArtifact(string name) =>
        Artifacts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>Reads the free-form payload as <typeparamref name="T"/>; null if the manifest has none.</summary>
    public T? ReadPayload<T>()
    {
        if (Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        try
        {
            return Payload.Deserialize<T>(CoreJson.Options);
        }
        catch (JsonException ex)
        {
            throw new BootrixException(ErrorCode.SignatureInvalid, "payload does not have the expected shape: " + ex.Message, ex);
        }
    }
}
