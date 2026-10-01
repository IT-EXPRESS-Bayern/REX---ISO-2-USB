// SPDX-License-Identifier: GPL-3.0-or-later
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Bootrix.Core.Boot;

namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// A throw-away CA with a signing certificate that writes Authenticode signature blocks in pure .NET, so the signature
/// logic can be tested on machines without osslsigncode. The blocks are cross-checked against osslsigncode elsewhere.
/// </summary>
public sealed class TestAuthority : IDisposable
{
    public const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    public const string NestedSignatureOid = "1.3.6.1.4.1.311.2.4.1";

    private static readonly Dictionary<string, string> DigestOids = new()
    {
        ["SHA1"] = "1.3.14.3.2.26",
        ["SHA256"] = "2.16.840.1.101.3.4.2.1",
        ["SHA384"] = "2.16.840.1.101.3.4.2.2",
        ["SHA512"] = "2.16.840.1.101.3.4.2.3",
    };

    private TestAuthority(X509Certificate2 ca, X509Certificate2 signer)
    {
        Ca = ca;
        Signer = signer;
    }

    public static string DigestOid(string digest) => DigestOids[digest];

    public X509Certificate2 Ca { get; }

    /// <summary>The signing certificate, issued by <see cref="Ca"/>, with its private key.</summary>
    public X509Certificate2 Signer { get; }

    public static TestAuthority Create(string caName = "Bootrix Test CA", string signerName = "Bootrix Test Signer")
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=" + caName, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        using var signerKey = RSA.Create(2048);
        var signerRequest = new CertificateRequest("CN=" + signerName, signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signerPublic = signerRequest.Create(ca, now.AddDays(-1), now.AddYears(5), RandomNumberGenerator.GetBytes(8));

        // Both are re-imported from their DER form so that they stay valid after the temporary keys are disposed.
        return new TestAuthority(
            X509CertificateLoader.LoadCertificate(ca.RawData),
            signerPublic.CopyWithPrivateKey(signerKey).CopyWithPrivateKeyExportable());
    }

    /// <summary>
    /// Builds the PKCS#7 block of a signature over <paramref name="image"/>. With <paramref name="nestedBlock"/> the block is
    /// attached to the signer as nested signature (OID 1.3.6.1.4.1.311.2.4.1), the way dual SHA-1/SHA-256 signatures are stored.
    /// </summary>
    public byte[] Sign(byte[] image, string digest = "SHA256", bool includeCa = true, byte[]? nestedBlock = null)
    {
        var imageDigest = EfiBinary.Parse(image).ComputeAuthenticodeHash(new HashAlgorithmName(digest));
        return SignDigest(imageDigest, digest, includeCa, nestedBlock);
    }

    public byte[] SignDigest(byte[] imageDigest, string digest = "SHA256", bool includeCa = true, byte[]? nestedBlock = null, IEnumerable<X509Certificate2>? extraCertificates = null)
    {
        var block = SignContent(SpcIndirectDataOid, IndirectData(imageDigest, DigestOids[digest]), digest, includeCa, extraCertificates);
        if (nestedBlock is null)
        {
            return block;
        }

        var cms = new SignedCms();
        cms.Decode(block);
        cms.SignerInfos[0].AddUnsignedAttribute(new AsnEncodedData(new Oid(NestedSignatureOid), nestedBlock));
        return cms.Encode();
    }

    /// <summary>Signs arbitrary content under an arbitrary content type, for blocks that are not valid Authenticode.</summary>
    public byte[] SignContent(string contentType, byte[] content, string digest = "SHA256", bool includeCa = true, IEnumerable<X509Certificate2>? extraCertificates = null)
    {
        var cms = new SignedCms(new ContentInfo(new Oid(contentType), content), detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Signer)
        {
            DigestAlgorithm = new Oid(DigestOids[digest]),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        if (includeCa)
        {
            signer.Certificates.Add(Ca);
        }

        foreach (var certificate in extraCertificates ?? [])
        {
            signer.Certificates.Add(certificate);
        }

        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    /// <summary>SpcIndirectDataContent: an image data placeholder followed by the DigestInfo of the image hash.</summary>
    public static byte[] IndirectData(byte[] imageDigest, string digestOid)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15"); // SPC_PE_IMAGE_DATAOBJ
                using (writer.PushSequence())
                {
                }
            }

            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(digestOid);
                    writer.WriteNull();
                }

                writer.WriteOctetString(imageDigest);
            }
        }

        return writer.Encode();
    }

    public void Dispose()
    {
        Ca.Dispose();
        Signer.Dispose();
    }
}

internal static class X509CertificateExtensions
{
    /// <summary>Round-trips through PKCS#12 so the private key is an exportable, self-contained copy that CmsSigner can use.</summary>
    public static X509Certificate2 CopyWithPrivateKeyExportable(this X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx, "x");
        certificate.Dispose();
        return X509CertificateLoader.LoadPkcs12(pfx, "x", X509KeyStorageFlags.Exportable);
    }
}
