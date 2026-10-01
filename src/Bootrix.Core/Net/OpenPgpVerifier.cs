// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;

namespace Bootrix.Core.Net;

/// <summary>
/// Verifies the signatures distributions put on their checksum files (detached <c>.gpg</c>/<c>.sig</c>/<c>.sign</c>
/// and clear-signed <c>CHECKSUM</c>/<c>InRelease</c>) in managed code, without gpg and without a key server.
/// </summary>
public static partial class OpenPgpVerifier
{
    private const int MinimumRsaBits = 2048;

    /// <summary>
    /// Checks a detached signature (binary or ASCII-armored) over <paramref name="data"/>.
    /// </summary>
    /// <param name="allowedFingerprints">
    /// If given, only keys whose primary (or own) fingerprint is listed can produce a valid result. Spaces and a
    /// leading 0x are ignored.
    /// </param>
    public static OpenPgpResult Verify(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature,
        OpenPgpKeyring keyring,
        IReadOnlyCollection<string>? allowedFingerprints = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(keyring);

        var signatures = ReadSignatures(signature, out var problem);
        return signatures.Count == 0
            ? new OpenPgpResult(OpenPgpStatus.Invalid, null, null, problem)
            : Decide(signatures, data.ToArray(), keyring, Normalize(allowedFingerprints), time ?? TimeProvider.System);
    }

    public static OpenPgpResult Verify(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> keyring,
        IReadOnlyCollection<string>? allowedFingerprints = null,
        TimeProvider? time = null) =>
        Verify(data, signature, OpenPgpKeyring.Load(keyring), allowedFingerprints, time);

    /// <summary>
    /// Checks a clear-signed document. Only <see cref="ClearSignedResult.Text"/> may be used afterwards; it is
    /// the part the signature covers.
    /// </summary>
    public static ClearSignedResult VerifyClearSigned(
        ReadOnlySpan<byte> document,
        OpenPgpKeyring keyring,
        IReadOnlyCollection<string>? allowedFingerprints = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(keyring);

        if (!ClearSignedDocument.TryParse(document, out var parsed, out var reason))
        {
            return new ClearSignedResult(new OpenPgpResult(OpenPgpStatus.Invalid, null, null, reason), string.Empty);
        }

        var signatures = ReadSignatures(parsed!.SignatureArmor, out var problem);
        var result = signatures.Count == 0
            ? new OpenPgpResult(OpenPgpStatus.Invalid, null, null, problem)
            : Decide(signatures, parsed.CanonicalText, keyring, Normalize(allowedFingerprints), time ?? TimeProvider.System);

        return new ClearSignedResult(result, result.IsValid ? parsed.Text : string.Empty);
    }

    public static ClearSignedResult VerifyClearSigned(
        ReadOnlySpan<byte> document,
        ReadOnlySpan<byte> keyring,
        IReadOnlyCollection<string>? allowedFingerprints = null,
        TimeProvider? time = null) =>
        VerifyClearSigned(document, OpenPgpKeyring.Load(keyring), allowedFingerprints, time);

    private static OpenPgpResult Decide(
        List<PgpSignature> signatures,
        byte[] data,
        OpenPgpKeyring keyring,
        HashSet<string>? allowed,
        TimeProvider time)
    {
        OpenPgpResult? worst = null;

        foreach (var signature in signatures)
        {
            var result = Evaluate(signature, data, keyring, allowed, time.GetUtcNow().UtcDateTime);
            if (result.IsValid)
            {
                return result;
            }

            if (worst is null || Rank(result.Status) < Rank(worst.Status))
            {
                worst = result;
            }
        }

        return worst!;
    }

    private static OpenPgpResult Evaluate(PgpSignature signature, byte[] data, OpenPgpKeyring keyring, HashSet<string>? allowed, DateTime now)
    {
        var keyId = signature.KeyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        var candidates = keyring.Find(signature.KeyId).ToList();
        if (candidates.Count == 0)
        {
            return new OpenPgpResult(OpenPgpStatus.KeyUnknown, null, keyId, "the signing key is not in the keyring");
        }

        var fingerprint = Convert.ToHexString(candidates[0].Primary.GetFingerprint());
        OpenPgpResult Fail(OpenPgpStatus status, string reason) => new(status, fingerprint, keyId, reason);

        // A revocation in any copy of the key counts, however the keyring got assembled.
        if (candidates.Any(c => c.Key.HasRevocation() || c.Primary.HasRevocation()))
        {
            return Fail(OpenPgpStatus.KeyRevoked, "the signing key has been revoked");
        }

        if (signature.SignatureType is not (PgpSignature.BinaryDocument or PgpSignature.CanonicalTextDocument))
        {
            return Fail(OpenPgpStatus.Invalid, $"signature type 0x{signature.SignatureType:X2} does not sign documents");
        }

        if (signature.HashAlgorithm is HashAlgorithmTag.MD5 or HashAlgorithmTag.Sha1 or HashAlgorithmTag.RipeMD160)
        {
            return Fail(OpenPgpStatus.Invalid, $"{signature.HashAlgorithm} is not accepted for signatures");
        }

        OpenPgpResult? rejected = null;
        foreach (var (key, primary) in candidates)
        {
            var status = Precheck(key, primary, signature, allowed, now);
            if (status is { } failure)
            {
                rejected ??= Fail(failure.Status, failure.Reason);
                continue;
            }

            return Verify(signature, key, data) ? new OpenPgpResult(OpenPgpStatus.Valid, Convert.ToHexString(primary.GetFingerprint()), keyId) : Fail(OpenPgpStatus.Invalid, "the signature does not match the data");
        }

        return rejected!;
    }

    private static (OpenPgpStatus Status, string Reason)? Precheck(PgpPublicKey key, PgpPublicKey primary, PgpSignature signature, HashSet<string>? allowed, DateTime now)
    {
        if (allowed is not null
            && !allowed.Contains(Convert.ToHexString(primary.GetFingerprint()))
            && !allowed.Contains(Convert.ToHexString(key.GetFingerprint())))
        {
            return (OpenPgpStatus.KeyNotAllowed, "the key is not among the pinned fingerprints");
        }

        if (IsExpired(key, now) || IsExpired(primary, now))
        {
            return (OpenPgpStatus.KeyExpired, "the signing key has expired");
        }

        if (signature.CreationTime < key.CreationTime)
        {
            return (OpenPgpStatus.Invalid, "the signature is older than the key");
        }

        if (key.Algorithm is PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign && key.BitStrength < MinimumRsaBits)
        {
            return (OpenPgpStatus.Invalid, $"RSA key of {key.BitStrength} bits is too weak");
        }

        return null;
    }

    private static bool Verify(PgpSignature signature, PgpPublicKey key, byte[] data)
    {
        try
        {
            signature.InitVerify(key);
            signature.Update(data);
            return signature.Verify();
        }
        catch (Exception ex) when (ex is PgpException or CryptoException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsExpired(PgpPublicKey key, DateTime now)
    {
        var seconds = key.GetValidSeconds();
        return seconds > 0 && key.CreationTime.AddSeconds(seconds) <= now;
    }

    private static List<PgpSignature> ReadSignatures(ReadOnlySpan<byte> data, out string? problem)
    {
        problem = null;
        var signatures = new List<PgpSignature>();

        try
        {
            var text = Encoding.ASCII.GetString(data[..Math.Min(data.Length, 64)]).TrimStart();
            var blocks = text.StartsWith("-----BEGIN PGP", StringComparison.Ordinal)
                ? ArmoredSignature().Matches(Encoding.ASCII.GetString(data)).Select(m => Encoding.ASCII.GetBytes(m.Value)).ToList()
                : [data.ToArray()];

            foreach (var block in blocks)
            {
                using var input = new MemoryStream(block);
                using var decoder = PgpUtilities.GetDecoderStream(input);
                var factory = new PgpObjectFactory(decoder);
                for (var next = factory.NextPgpObject(); next is not null; next = factory.NextPgpObject())
                {
                    if (next is PgpSignatureList list)
                    {
                        for (var i = 0; i < list.Count; i++)
                        {
                            signatures.Add(list[i]);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is PgpException or IOException or ArgumentException or InvalidOperationException or FormatException)
        {
            problem = "the signature cannot be parsed: " + ex.Message;
            return [];
        }

        problem = signatures.Count == 0 ? "no signature found" : null;
        return signatures;
    }

    private static HashSet<string>? Normalize(IReadOnlyCollection<string>? fingerprints) =>
        fingerprints is null
            ? null
            : [.. fingerprints.Select(f => f.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant())];

    /// <summary>The most informative reason first: a revoked key says more than an unknown one.</summary>
    private static int Rank(OpenPgpStatus status) => status switch
    {
        OpenPgpStatus.KeyRevoked => 0,
        OpenPgpStatus.KeyExpired => 1,
        OpenPgpStatus.KeyNotAllowed => 2,
        OpenPgpStatus.Invalid => 3,
        _ => 4,
    };

    [GeneratedRegex(@"-----BEGIN PGP SIGNATURE-----.*?-----END PGP SIGNATURE-----", RegexOptions.Singleline)]
    private static partial Regex ArmoredSignature();
}
