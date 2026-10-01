// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Bootrix.Core.Net;

/// <summary>
/// An ECDSA P-384 public key that may sign manifests, identified by a short name. Bootrix carries two:
/// the current one and the next one, so a key can be rotated without a release that has to be trusted blindly.
/// </summary>
public sealed class ManifestPublicKey
{
    private const string P384Oid = "1.3.132.0.34";

    private readonly byte[] _subjectPublicKeyInfo;

    /// <param name="keyId">Letters, digits, dot, dash and underscore; the manifest envelope names the signing key with it.</param>
    /// <param name="subjectPublicKeyInfo">DER encoding as written by <c>openssl ec -pubout -outform DER</c>.</param>
    public ManifestPublicKey(string keyId, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (!keyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new ArgumentException("Key IDs may only contain letters, digits, '.', '-' and '_'.", nameof(keyId));
        }

        KeyId = keyId;
        _subjectPublicKeyInfo = subjectPublicKeyInfo.ToArray();

        using var key = CreateKey();
        if (key.ExportParameters(false).Curve.Oid.Value != P384Oid)
        {
            throw new ArgumentException("The key is not an ECDSA key on curve P-384.", nameof(subjectPublicKeyInfo));
        }
    }

    public string KeyId { get; }

    public static ManifestPublicKey FromBase64(string keyId, string subjectPublicKeyInfoBase64) =>
        new(keyId, Convert.FromBase64String(subjectPublicKeyInfoBase64));

    /// <summary>The caller disposes the key; it is created per verification so that this type stays a plain value holder.</summary>
    internal ECDsa CreateKey()
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
