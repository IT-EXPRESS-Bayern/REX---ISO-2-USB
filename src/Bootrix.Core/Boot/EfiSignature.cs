// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot;

/// <summary>Identity of one certificate of a signature chain. Thumbprints are lower-case hex.</summary>
public sealed record EfiCertificateInfo(
    string Subject,
    string CommonName,
    string Issuer,
    string Sha1Thumbprint,
    string Sha256Thumbprint,
    string TbsSha256Thumbprint);

/// <summary>
/// One signature of a PE image. Images with two certificate table entries or a nested signature
/// (OID 1.3.6.1.4.1.311.2.4.1) yield one instance per signer.
/// </summary>
/// <param name="Authority">The Microsoft CA found by walking the chain from the signer, or Other.</param>
/// <param name="Signer">The certificate that made the signature; null when it is not part of the PKCS#7 data.</param>
/// <param name="Chain">Signer first, then every issuer that could be linked cryptographically.</param>
/// <param name="DigestAlgorithm">Algorithm of the image digest inside the signature (SHA256, SHA1, ...).</param>
/// <param name="DigestMatchesImage">Whether that digest equals the Authenticode hash computed from the file.</param>
/// <param name="SignatureValid">Whether the PKCS#7 signature itself verifies against the signer's key.</param>
/// <param name="IsNested">True for signatures found in the nested-signature attribute.</param>
/// <param name="Problem">Technical description when the structure could not be evaluated.</param>
/// <param name="RevokedCertificate">Common name of the first chain certificate that the revocation data lists, if any.</param>
public sealed record EfiSignature(
    SignatureAuthority Authority,
    EfiCertificateInfo? Signer,
    IReadOnlyList<EfiCertificateInfo> Chain,
    string? DigestAlgorithm,
    bool DigestMatchesImage,
    bool SignatureValid,
    bool IsNested,
    string? Problem = null,
    string? RevokedCertificate = null)
{
    /// <summary>A signature firmware would accept as far as the file content is concerned.</summary>
    public bool IsIntact => SignatureValid && DigestMatchesImage;
}
