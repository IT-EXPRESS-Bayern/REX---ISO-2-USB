// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Bootrix.Core.Net;

/// <summary>Digest algorithms found in checksum files and metalinks, ordered from weakest to strongest.</summary>
public enum HashKind
{
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512,
}

internal static class HashKinds
{
    /// <summary>MD5 is parsed so that files listing it do not fail, but it is never accepted as proof of integrity.</summary>
    public static bool IsAcceptedForVerification(this HashKind kind) => kind != HashKind.Md5;

    public static int DigestLength(this HashKind kind) => kind switch
    {
        HashKind.Md5 => 16,
        HashKind.Sha1 => 20,
        HashKind.Sha256 => 32,
        HashKind.Sha384 => 48,
        HashKind.Sha512 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static HashAlgorithmName AlgorithmName(this HashKind kind) => kind switch
    {
        HashKind.Md5 => HashAlgorithmName.MD5,
        HashKind.Sha1 => HashAlgorithmName.SHA1,
        HashKind.Sha256 => HashAlgorithmName.SHA256,
        HashKind.Sha384 => HashAlgorithmName.SHA384,
        HashKind.Sha512 => HashAlgorithmName.SHA512,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Guesses the algorithm of a bare hex digest from its length. Ambiguous for BLAKE2/BLAKE3,
    /// which is why sectioned files and BSD-style lines name the algorithm explicitly.
    /// </summary>
    public static HashKind? FromHexLength(int hexLength) => hexLength switch
    {
        32 => HashKind.Md5,
        40 => HashKind.Sha1,
        64 => HashKind.Sha256,
        96 => HashKind.Sha384,
        128 => HashKind.Sha512,
        _ => null,
    };

    /// <summary>Accepts the spellings seen in the wild: <c>sha256</c>, <c>SHA-256</c>, <c>SHA256SUMS</c>.</summary>
    public static bool TryParseName(string name, out HashKind kind)
    {
        var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (normalized.EndsWith("SUMS", StringComparison.Ordinal))
        {
            normalized = normalized[..^4];
        }
        else if (normalized.EndsWith("SUM", StringComparison.Ordinal))
        {
            normalized = normalized[..^3];
        }

        HashKind? parsed = normalized switch
        {
            "MD5" => HashKind.Md5,
            "SHA1" => HashKind.Sha1,
            "SHA256" => HashKind.Sha256,
            "SHA384" => HashKind.Sha384,
            "SHA512" => HashKind.Sha512,
            _ => null,
        };

        kind = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }

    public static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
