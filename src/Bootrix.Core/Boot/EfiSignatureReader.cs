// SPDX-License-Identifier: GPL-3.0-or-later
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Bootrix.Core.Boot;

/// <summary>
/// Extracts the signatures of an EFI binary: every WIN_CERTIFICATE of PKCS#7 type plus the signatures nested
/// inside them. Firmware validates against db entries without checking dates or revocation online, so the
/// same is done here; what is checked is that the signature covers this file and which Microsoft CA stands
/// behind the signer.
/// </summary>
internal sealed class EfiSignatureReader(IReadOnlyList<X509Certificate2> pinnedAuthorities)
{
    private const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    private const string NestedSignatureOid = "1.3.6.1.4.1.311.2.4.1";
    private const int MaxNestingDepth = 3;
    private const int MaxSignatures = 32;
    private const int MaxChainLength = 8;

    private static readonly Dictionary<string, (string Name, HashAlgorithmName Algorithm)> DigestAlgorithms = new()
    {
        ["1.3.14.3.2.26"] = ("SHA1", HashAlgorithmName.SHA1),
        ["2.16.840.1.101.3.4.2.1"] = ("SHA256", HashAlgorithmName.SHA256),
        ["2.16.840.1.101.3.4.2.2"] = ("SHA384", HashAlgorithmName.SHA384),
        ["2.16.840.1.101.3.4.2.3"] = ("SHA512", HashAlgorithmName.SHA512),
    };

    private sealed class ReadContext(EfiBinary binary, CancellationToken cancellationToken)
    {
        public EfiBinary Binary { get; } = binary;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public List<EfiSignature> Signatures { get; } = [];

        public Dictionary<string, byte[]> ImageHashes { get; } = [];
    }

    public IReadOnlyList<EfiSignature> Read(EfiBinary binary, CancellationToken cancellationToken)
    {
        var context = new ReadContext(binary, cancellationToken);
        foreach (var certificate in binary.Certificates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (certificate.Type == WinCertificate.TypePkcsSignedData)
            {
                ReadSignedData(certificate.Data, 0, context);
            }
            else
            {
                AddProblem(context, false, "unsupported certificate type 0x" + certificate.Type.ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return context.Signatures;
    }

    private static EfiCertificateInfo Describe(X509Certificate2 certificate)
    {
        var tbs = CertificateSignature.GetTbsCertificate(certificate);
        return new EfiCertificateInfo(
            certificate.Subject,
            certificate.GetNameInfo(X509NameType.SimpleName, false),
            certificate.Issuer,
            Convert.ToHexStringLower(certificate.GetCertHash()),
            Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)),
            tbs is { } value ? Convert.ToHexStringLower(SHA256.HashData(value.Span)) : string.Empty);
    }

    private static void AddProblem(ReadContext context, bool nested, string problem)
    {
        if (context.Signatures.Count < MaxSignatures)
        {
            context.Signatures.Add(new EfiSignature(SignatureAuthority.Other, null, [], null, false, false, nested, problem));
        }
    }

    private static bool TryReadImageDigest(ReadOnlyMemory<byte> content, out string algorithmOid, out byte[] digest)
    {
        algorithmOid = string.Empty;
        digest = [];
        try
        {
            // SpcIndirectDataContent: SEQUENCE { data SpcAttributeTypeAndOptionalValue, messageDigest DigestInfo }
            var indirect = new AsnReader(content, AsnEncodingRules.BER).ReadSequence();
            indirect.ReadEncodedValue();
            var digestInfo = indirect.ReadSequence();
            algorithmOid = digestInfo.ReadSequence().ReadObjectIdentifier();
            digest = digestInfo.ReadOctetString();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private void ReadSignedData(ReadOnlyMemory<byte> data, int depth, ReadContext context)
    {
        var nested = depth > 0;
        var owned = new List<X509Certificate2>();
        try
        {
            // The table pads each blob to 8 bytes and some producers include that padding in the length.
            AsnDecoder.ReadEncodedValue(data.Span, AsnEncodingRules.BER, out _, out _, out var consumed);
            var cms = new SignedCms();
            cms.Decode(data.Span[..consumed]);

            if (cms.ContentInfo.ContentType.Value != SpcIndirectDataOid
                || !TryReadImageDigest(cms.ContentInfo.Content, out var digestOid, out var digest))
            {
                AddProblem(context, nested, "signed content is not an Authenticode SpcIndirectDataContent");
                return;
            }

            foreach (var certificate in cms.Certificates)
            {
                owned.Add(certificate);
            }

            var digestMatches = DigestMatches(context, digestOid, digest);
            var digestName = DigestAlgorithms.TryGetValue(digestOid, out var known) ? known.Name : digestOid;

            foreach (var signer in cms.SignerInfos)
            {
                if (context.Signatures.Count >= MaxSignatures)
                {
                    return;
                }

                var signerCertificate = signer.Certificate;
                if (signerCertificate is not null)
                {
                    owned.Add(signerCertificate);
                }

                context.Signatures.Add(Evaluate(signer, signerCertificate, owned, digestName, digestMatches, nested));
                if (depth < MaxNestingDepth)
                {
                    ReadNestedSignatures(signer, depth, context);
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or InvalidOperationException or ArgumentException)
        {
            // Lazily parsed parts of a damaged block (certificates, signer infos) throw here and not in Decode.
            AddProblem(context, nested, "signature data cannot be evaluated: " + ex.Message);
        }
        finally
        {
            foreach (var certificate in owned)
            {
                certificate.Dispose();
            }
        }
    }

    private void ReadNestedSignatures(SignerInfo signer, int depth, ReadContext context)
    {
        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value != NestedSignatureOid)
            {
                continue;
            }

            foreach (var value in attribute.Values)
            {
                ReadSignedData(value.RawData, depth + 1, context);
            }
        }
    }

    private static bool DigestMatches(ReadContext context, string digestOid, byte[] digest)
    {
        if (!DigestAlgorithms.TryGetValue(digestOid, out var known))
        {
            return false;
        }

        if (!context.ImageHashes.TryGetValue(digestOid, out var computed))
        {
            computed = context.Binary.ComputeAuthenticodeHash(known.Algorithm, context.CancellationToken);
            context.ImageHashes[digestOid] = computed;
        }

        return CryptographicOperations.FixedTimeEquals(computed, digest);
    }

    private EfiSignature Evaluate(
        SignerInfo signer,
        X509Certificate2? signerCertificate,
        List<X509Certificate2> bag,
        string digestName,
        bool digestMatches,
        bool nested)
    {
        if (signerCertificate is null)
        {
            return new EfiSignature(SignatureAuthority.Other, null, [], digestName, digestMatches, false, nested, "signer certificate is not part of the signature");
        }

        var signatureValid = true;
        try
        {
            signer.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            signatureValid = false;
        }

        var chain = BuildChain(signerCertificate, bag);
        var authority = SignatureAuthority.Other;
        foreach (var certificate in chain)
        {
            var known = MicrosoftCertificateAuthorities.FindBySha1(Convert.ToHexString(certificate.GetCertHash()));
            if (known is not null)
            {
                authority = known.Authority;
                break;
            }
        }

        var description = chain.Select(Describe).ToList();
        return new EfiSignature(authority, description[0], description, digestName, digestMatches, signatureValid, nested);
    }

    private List<X509Certificate2> BuildChain(X509Certificate2 signer, List<X509Certificate2> bag)
    {
        var chain = new List<X509Certificate2> { signer };
        var current = signer;
        while (chain.Count < MaxChainLength)
        {
            var issuer = FindIssuer(current, bag) ?? FindIssuer(current, pinnedAuthorities);
            if (issuer is null || chain.Any(c => c.Thumbprint == issuer.Thumbprint))
            {
                break;
            }

            chain.Add(issuer);
            current = issuer;
        }

        return chain;
    }

    private static X509Certificate2? FindIssuer(X509Certificate2 certificate, IEnumerable<X509Certificate2> candidates) =>
        candidates.FirstOrDefault(c =>
            c.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
            && CertificateSignature.IsSignedBy(certificate, c));
}
