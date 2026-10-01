// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bootrix.Core.Boot;

internal sealed record MicrosoftCertificateAuthority(
    SignatureAuthority Authority,
    string Sha1Thumbprint,
    string Sha256Thumbprint);

/// <summary>
/// The CAs that Secure Boot firmware trusts for Microsoft signed boot binaries. The thumbprints were taken from the
/// certificates published in the Microsoft PKI repository (https://www.microsoft.com/pkiops/certs/) and compared with
/// microsoft/secureboot_objects v1.7.0 (PreSignedObjects/DB/Certificates); the SHA-1 values of the first four also
/// match the list in Rufus (src/db.h).
/// </summary>
internal static class MicrosoftCertificateAuthorities
{
    public static IReadOnlyList<MicrosoftCertificateAuthority> All { get; } =
    [
        new(
            SignatureAuthority.WindowsProductionPca2011,
            "580a6f4cc4e4b669b9ebdc1b2b3e087b80d0678d",
            "e8e95f0733a55e8bad7be0a1413ee23c51fcea64b3c8fa6a786935fddcc71961"),
        new(
            SignatureAuthority.MicrosoftUefiCa2011,
            "46def63b5ce61cf8ba0de2e6639c1019d0ed14f3",
            "48e99b991f57fc52f76149599bff0a58c47154229b9f8d603ac40d3500248507"),
        new(
            SignatureAuthority.WindowsUefiCa2023,
            "45a0fa32604773c82433c3b7d59e7466b3ac0c67",
            "076f1fea90ac29155ebf77c17682f75f1fdd1be196da302dc8461e350a9ae330"),
        new(
            SignatureAuthority.MicrosoftUefiCa2023,
            "b5eeb4a6706048073f0ed296e7f580a790b59eaa",
            "f6124e34125bee3fe6d79a574eaa7b91c0e7bd9d929c1a321178efd611dad901"),
        new(
            SignatureAuthority.MicrosoftOptionRomUefiCa2023,
            "3fb39e2b8bd183bf9e4594e72183ca60afcd4277",
            "e5be3e64c6e66a281457ecdece0d6d0787577aad2a3a0144262c10c14ba8d8f1"),
    ];

    public static MicrosoftCertificateAuthority? FindBySha1(string thumbprint) =>
        All.FirstOrDefault(ca => string.Equals(ca.Sha1Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads the DER certificates of a snapshot, keeping only those whose thumbprints match the constants above,
    /// so a damaged or tampered snapshot cannot introduce a CA that the code does not know.
    /// </summary>
    public static List<X509Certificate2> LoadPinned(IEnumerable<byte[]> derCertificates)
    {
        var result = new List<X509Certificate2>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var der in derCertificates)
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(der));
            if (All.All(ca => ca.Sha256Thumbprint != sha256) || !seen.Add(sha256))
            {
                continue;
            }

            result.Add(X509CertificateLoader.LoadCertificate(der));
        }

        return result;
    }
}
