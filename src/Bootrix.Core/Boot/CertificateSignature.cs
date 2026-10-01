// SPDX-License-Identifier: GPL-3.0-or-later
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bootrix.Core.Boot;

/// <summary>
/// Checks whether a certificate was signed by a given issuer certificate. The platform chain engines are not
/// used because the outcome would depend on the operating system's trust store, on revocation settings and on
/// certificate validity dates; firmware ignores all three and only asks "does this key sign that certificate".
/// </summary>
internal static class CertificateSignature
{
    private static readonly Dictionary<string, (HashAlgorithmName Hash, bool Rsa)> Algorithms = new()
    {
        ["1.2.840.113549.1.1.5"] = (HashAlgorithmName.SHA1, true),
        ["1.2.840.113549.1.1.11"] = (HashAlgorithmName.SHA256, true),
        ["1.2.840.113549.1.1.12"] = (HashAlgorithmName.SHA384, true),
        ["1.2.840.113549.1.1.13"] = (HashAlgorithmName.SHA512, true),
        ["1.2.840.10045.4.3.2"] = (HashAlgorithmName.SHA256, false),
        ["1.2.840.10045.4.3.3"] = (HashAlgorithmName.SHA384, false),
        ["1.2.840.10045.4.3.4"] = (HashAlgorithmName.SHA512, false),
    };

    /// <summary>The DER encoded TBSCertificate, the part of the certificate that the issuer signs.</summary>
    public static ReadOnlyMemory<byte>? GetTbsCertificate(X509Certificate2 certificate) =>
        TryRead(certificate, out var tbs, out _, out _) ? tbs : null;

    public static bool IsSignedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        if (!TryRead(certificate, out var tbs, out var algorithmOid, out var signature)
            || !Algorithms.TryGetValue(algorithmOid, out var algorithm))
        {
            return false;
        }

        try
        {
            if (algorithm.Rsa)
            {
                using var rsa = issuer.GetRSAPublicKey();
                return rsa is not null && rsa.VerifyData(tbs.Span, signature, algorithm.Hash, RSASignaturePadding.Pkcs1);
            }

            using var ecdsa = issuer.GetECDsaPublicKey();
            return ecdsa is not null
                && ecdsa.VerifyData(tbs.Span, signature, algorithm.Hash, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryRead(
        X509Certificate2 certificate,
        out ReadOnlyMemory<byte> tbs,
        out string algorithmOid,
        out byte[] signature)
    {
        tbs = default;
        algorithmOid = string.Empty;
        signature = [];
        try
        {
            var certificateSequence = new AsnReader(certificate.RawDataMemory, AsnEncodingRules.DER).ReadSequence();
            tbs = certificateSequence.ReadEncodedValue();
            algorithmOid = certificateSequence.ReadSequence().ReadObjectIdentifier();
            signature = certificateSequence.ReadBitString(out _);
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}
