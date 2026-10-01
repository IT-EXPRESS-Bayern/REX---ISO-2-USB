// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>A digest as written in a checksum file: algorithm plus lower-case hex.</summary>
public sealed record FileHash
{
    public FileHash(HashKind kind, string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (!IsWellFormed(kind, hex))
        {
            throw new ArgumentException($"Not a valid {kind} digest: '{hex}'.", nameof(hex));
        }

        Kind = kind;
        Hex = hex.ToLowerInvariant();
    }

    public HashKind Kind { get; }

    public string Hex { get; }

    public static bool TryCreate(HashKind kind, string? hex, out FileHash? hash)
    {
        if (hex is not null && IsWellFormed(kind, hex))
        {
            hash = new FileHash(kind, hex);
            return true;
        }

        hash = null;
        return false;
    }

    public bool Matches(ReadOnlySpan<byte> digest) =>
        digest.Length == Kind.DigestLength()
        && string.Equals(Convert.ToHexStringLower(digest), Hex, StringComparison.Ordinal);

    public override string ToString() => $"{Kind}:{Hex}";

    private static bool IsWellFormed(HashKind kind, string hex) =>
        hex.Length == kind.DigestLength() * 2 && HashKinds.IsHex(hex);
}
