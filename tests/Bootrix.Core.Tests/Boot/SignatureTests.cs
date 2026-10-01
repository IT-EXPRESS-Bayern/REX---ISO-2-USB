// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Bootrix.Core.Boot;

namespace Bootrix.Core.Tests.Boot;

public sealed class AuthorityFixture : IDisposable
{
    public TestAuthority Own { get; } = TestAuthority.Create();

    /// <summary>A CA that merely carries the name of a Microsoft CA.</summary>
    public TestAuthority Lookalike { get; } = TestAuthority.Create("Microsoft Corporation UEFI CA 2011", "Microsoft Windows UEFI Driver Publisher");

    public void Dispose()
    {
        Own.Dispose();
        Lookalike.Dispose();
    }
}

/// <summary>
/// Signature evaluation. The signature blocks of the real Microsoft, Debian, Ubuntu and AlmaLinux binaries are used
/// as they are (the blocks cannot match the small synthetic images, which the report states), the other cases are
/// signed on the fly: in pure .NET, and cross-checked with osslsigncode and sbsign where those are installed.
/// </summary>
public class SignatureTests(AuthorityFixture authorities, SigningFixture signing) : IClassFixture<AuthorityFixture>, IClassFixture<SigningFixture>
{
    private const string DebianShimHash = "9E9A31DB05A5DF7F3AC1DEF4633EAE26FB2EAD788C0642FDDCC1030676A2B287";

    private static EfiFileReport Analyze(byte[] image, string path = "EFI/BOOT/BOOTX64.EFI") =>
        new EfiMediaAnalyzer().Analyze([(path, new MemoryStream(image))]).Files.Single();

    private static byte[] WithRealSignature(string fixture) => PeBuilder.Typical().AddCertificate(Fixtures.Read(fixture)).Build();

    [Fact]
    public void RealSignature_MicrosoftUefiCa2011_IsRecognised()
    {
        var signature = Assert.Single(Analyze(WithRealSignature("ms-uefi-ca-2011.p7")).Signatures);

        Assert.Equal(SignatureAuthority.MicrosoftUefiCa2011, signature.Authority);
        Assert.Equal("Microsoft Windows UEFI Driver Publisher", signature.Signer?.CommonName);
        Assert.Equal("SHA256", signature.DigestAlgorithm);
        Assert.True(signature.SignatureValid);
        Assert.False(signature.IsNested);
        Assert.Null(signature.Problem);
        Assert.Equal(["Microsoft Windows UEFI Driver Publisher", "Microsoft Corporation UEFI CA 2011"], signature.Chain.Select(c => c.CommonName));
        Assert.Equal("46def63b5ce61cf8ba0de2e6639c1019d0ed14f3", signature.Chain[1].Sha1Thumbprint);
        Assert.Equal("48e99b991f57fc52f76149599bff0a58c47154229b9f8d603ac40d3500248507", signature.Chain[1].Sha256Thumbprint);
        Assert.Equal("78445f8373dd4a171e00c9d968a533fb4dfab391", signature.Chain[0].Sha1Thumbprint);
    }

    [Fact]
    public void RealSignature_TransplantedOntoAnotherImage_DoesNotMatchTheImage()
    {
        var signature = Assert.Single(Analyze(WithRealSignature("ms-uefi-ca-2011.p7")).Signatures);

        Assert.False(signature.DigestMatchesImage);
        Assert.False(signature.IsIntact);
    }

    [Fact]
    public void RealSignature_MicrosoftUefiCa2023_IsRecognised()
    {
        var signature = Assert.Single(Analyze(WithRealSignature("ms-uefi-ca-2023.p7")).Signatures);

        Assert.Equal(SignatureAuthority.MicrosoftUefiCa2023, signature.Authority);
        Assert.Equal("Microsoft UEFI CA 2023 signer", signature.Signer?.CommonName);
        Assert.Equal("b5eeb4a6706048073f0ed296e7f580a790b59eaa", signature.Chain[1].Sha1Thumbprint);
    }

    [Fact]
    public void RealSignature_WindowsProductionPca2011_IsRecognisedAndRevokedBySnapshot()
    {
        var report = Analyze(WithRealSignature("ms-windows-pca-2011.p7"), "EFI/Microsoft/Boot/bootmgfw.efi");

        var signature = Assert.Single(report.Signatures);
        Assert.Equal(SignatureAuthority.WindowsProductionPca2011, signature.Authority);
        Assert.Equal("Microsoft Windows", signature.Signer?.CommonName);
        Assert.Equal("Microsoft Windows Production PCA 2011", signature.RevokedCertificate);
        Assert.Contains(report.Revocations, r => r.Kind == RevocationKind.Certificate);
    }

    [Fact]
    public void RealSignature_CanonicalGrub_IsOtherThanMicrosoft()
    {
        var signature = Assert.Single(Analyze(WithRealSignature("canonical-grub.p7"), "EFI/ubuntu/grubx64.efi").Signatures);

        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.Equal("Canonical Ltd. Secure Boot Signing (2022 v1)", signature.Signer?.CommonName);
        Assert.Single(signature.Chain);
    }

    [Fact]
    public void RealSignatures_TwoCertificateTableEntries_AreBothReported()
    {
        // Dual-signed shims (2011 and 2023) store the two signatures as two WIN_CERTIFICATE entries.
        var image = PeBuilder.Typical()
            .AddCertificate(Fixtures.Read("ms-uefi-ca-2011.p7"))
            .AddCertificate(Fixtures.Read("ms-uefi-ca-2023.p7"))
            .Build();

        var report = Analyze(image);

        Assert.Equal([SignatureAuthority.MicrosoftUefiCa2011, SignatureAuthority.MicrosoftUefiCa2023], report.Signatures.Select(s => s.Authority));
        Assert.Equal(report.Signatures.Select(s => s.Authority), report.Authorities);
    }

    [Fact]
    public void RealSignature_ChangedDigestInTheSignedContent_FailsTheSignatureCheck()
    {
        var blob = Fixtures.Read("ms-uefi-ca-2011.p7");
        var digest = Convert.FromHexString(DebianShimHash);
        var at = blob.AsSpan().IndexOf(digest);
        Assert.True(at > 0);
        blob[at + 5] ^= 0x01;

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate(blob).Build()).Signatures);

        Assert.False(signature.SignatureValid);
        Assert.Equal(SignatureAuthority.MicrosoftUefiCa2011, signature.Authority);
    }

    [Fact]
    public void RealSignature_WithoutTheCaInTheBlock_IsStillClassifiedThroughTheEmbeddedCa()
    {
        var cms = new SignedCms();
        cms.Decode(Fixtures.Read("ms-uefi-ca-2011.p7"));
        var ca = cms.Certificates.Single(c => c.Subject.Contains("UEFI CA 2011", StringComparison.Ordinal));
        cms.RemoveCertificate(ca);
        Assert.Single(cms.Certificates);

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate(cms.Encode()).Build()).Signatures);

        Assert.Equal(SignatureAuthority.MicrosoftUefiCa2011, signature.Authority);
        Assert.Equal(2, signature.Chain.Count);
    }

    [Fact]
    public void RealSignature_WithTrailingZeroPadding_IsDecoded()
    {
        var blob = Fixtures.Read("ms-uefi-ca-2023.p7");

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate([.. blob, 0, 0, 0, 0, 0, 0]).Build()).Signatures);

        Assert.Equal(SignatureAuthority.MicrosoftUefiCa2023, signature.Authority);
    }

    [Fact]
    public void OwnCa_IntactSignature_IsClassifiedAsOtherAndMatchesTheImage()
    {
        var image = PeBuilder.Typical().Build();
        var signed = PeBuilder.Typical().AddCertificate(authorities.Own.Sign(image)).Build();

        var signature = Assert.Single(Analyze(signed).Signatures);

        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.True(signature.IsIntact);
        Assert.Equal("SHA256", signature.DigestAlgorithm);
        Assert.Equal(["Bootrix Test Signer", "Bootrix Test CA"], signature.Chain.Select(c => c.CommonName));
    }

    [Theory]
    [InlineData("SHA1")]
    [InlineData("SHA384")]
    [InlineData("SHA512")]
    public void OwnCa_OtherDigestAlgorithms_AreComparedWithTheirOwnImageHash(string digest)
    {
        var image = PeBuilder.Typical().Build();
        var signed = PeBuilder.Typical().AddCertificate(authorities.Own.Sign(image, digest)).Build();

        var signature = Assert.Single(Analyze(signed).Signatures);

        Assert.Equal(digest, signature.DigestAlgorithm);
        Assert.True(signature.IsIntact);
    }

    [Fact]
    public void OwnCa_ImageChangedAfterSigning_IsNoLongerIntact()
    {
        var block = authorities.Own.Sign(PeBuilder.Typical().Build());
        var signed = PeBuilder.Typical().AddCertificate(block).Build();
        signed[0x210] ^= 0x40;

        var signature = Assert.Single(Analyze(signed).Signatures);

        Assert.True(signature.SignatureValid);
        Assert.False(signature.DigestMatchesImage);
    }

    [Fact]
    public void OwnCa_SignerCertificateOnly_HasAChainOfOne()
    {
        var image = PeBuilder.Typical().Build();
        var signed = PeBuilder.Typical().AddCertificate(authorities.Own.Sign(image, includeCa: false)).Build();

        var signature = Assert.Single(Analyze(signed).Signatures);

        Assert.Single(signature.Chain);
        Assert.Equal(SignatureAuthority.Other, signature.Authority);
    }

    [Fact]
    public void Lookalike_CaWithAMicrosoftName_IsNotTakenForMicrosoft()
    {
        var image = PeBuilder.Typical().Build();
        var signed = PeBuilder.Typical().AddCertificate(authorities.Lookalike.Sign(image)).Build();

        var signature = Assert.Single(Analyze(signed).Signatures);

        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.Equal("Microsoft Corporation UEFI CA 2011", signature.Chain[1].CommonName);
        Assert.NotEqual("46def63b5ce61cf8ba0de2e6639c1019d0ed14f3", signature.Chain[1].Sha1Thumbprint);
    }

    [Fact]
    public void RealCaCertificateInTheBagDoesNotMakeAnUnrelatedSignatureMicrosoft()
    {
        var real = new SignedCms();
        real.Decode(Fixtures.Read("ms-uefi-ca-2011.p7"));
        var microsoftCa = real.Certificates.Single(c => c.Subject.Contains("UEFI CA 2011", StringComparison.Ordinal));

        var image = PeBuilder.Typical().Build();
        var block = authorities.Own.SignDigest(
            EfiBinary.Parse(image).ComputeAuthenticodeHash(System.Security.Cryptography.HashAlgorithmName.SHA256),
            extraCertificates: [microsoftCa]);

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate(block).Build()).Signatures);

        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.True(signature.IsIntact);
    }

    [Fact]
    public void NestedSignature_IsReportedNextToTheOuterOne()
    {
        var image = PeBuilder.Typical().Build();
        var inner = authorities.Own.Sign(image, "SHA256");
        var outer = authorities.Own.Sign(image, "SHA1", nestedBlock: inner);

        var signatures = Analyze(PeBuilder.Typical().AddCertificate(outer).Build()).Signatures;

        Assert.Equal(2, signatures.Count);
        Assert.Equal(["SHA1", "SHA256"], signatures.Select(s => s.DigestAlgorithm));
        Assert.Equal([false, true], signatures.Select(s => s.IsNested));
        Assert.All(signatures, s => Assert.True(s.IsIntact));
    }

    [Fact]
    public void NestedSignatures_DeeperThanTheLimit_AreCutOffWithoutFailing()
    {
        var image = PeBuilder.Typical().Build();
        var block = authorities.Own.Sign(image);
        for (var depth = 0; depth < 10; depth++)
        {
            block = authorities.Own.Sign(image, nestedBlock: block);
        }

        var signatures = Analyze(PeBuilder.Typical().AddCertificate(block).Build()).Signatures;

        Assert.InRange(signatures.Count, 2, 6);
    }

    [Fact]
    public void ManyCertificateEntries_AreCapped()
    {
        var builder = PeBuilder.Typical();
        for (var i = 0; i < 30; i++)
        {
            builder.AddCertificate([1, 2, 3, 4, 5]);
        }

        var report = Analyze(builder.Build());

        Assert.Equal(30, report.Signatures.Count);
        Assert.All(report.Signatures, s => Assert.NotNull(s.Problem));
    }

    [Fact]
    public void CertificateTableEntryOfAnotherType_IsReportedAsProblem()
    {
        var image = PeBuilder.Typical().AddCertificate([1, 2, 3, 4], type: 0x0EF1).Build();

        var signature = Assert.Single(Analyze(image).Signatures);

        Assert.Contains("0x0EF1", signature.Problem, StringComparison.Ordinal);
        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.False(signature.IsIntact);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(700)]
    [InlineData(5000)]
    public void TruncatedSignatureBlock_IsReportedAsProblem(int length)
    {
        var blob = Fixtures.Read("ms-uefi-ca-2011.p7");

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate(blob.AsSpan(0, length).ToArray()).Build()).Signatures);

        Assert.NotNull(signature.Problem);
        Assert.False(signature.IsIntact);
    }

    [Fact]
    public void SignedDataThatIsNotAuthenticode_IsReportedAsProblem()
    {
        var block = authorities.Own.SignContent("1.2.840.113549.1.7.1", [1, 2, 3]);

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate(block).Build()).Signatures);

        Assert.Contains("Authenticode", signature.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownImageDigestAlgorithm_IsNotAMatch()
    {
        var content = TestAuthority.IndirectData(new byte[16], "1.2.840.113549.2.5"); // MD5
        var block = authorities.Own.SignContent(TestAuthority.SpcIndirectDataOid, content);

        var signature = Assert.Single(Analyze(PeBuilder.Typical().AddCertificate(block).Build()).Signatures);

        Assert.Equal("1.2.840.113549.2.5", signature.DigestAlgorithm);
        Assert.False(signature.DigestMatchesImage);
        Assert.True(signature.SignatureValid);
    }

    [Fact]
    public void RandomlyDamagedSignatureBlocks_NeverEscapeAsOtherExceptions()
    {
        var original = Fixtures.Read("ms-uefi-ca-2011.p7");
        var random = new Random(4711);

        for (var round = 0; round < 400; round++)
        {
            var blob = (byte[])original.Clone();
            for (var change = 0; change < 1 + random.Next(4); change++)
            {
                blob[random.Next(blob.Length)] = (byte)random.Next(256);
            }

            var report = Analyze(PeBuilder.Typical().AddCertificate(blob).Build());

            Assert.NotEmpty(report.Signatures);
        }
    }

    [ToolFact("openssl", "osslsigncode")]
    public void Osslsigncode_Signature_IsIntactAndOther()
    {
        var image = PeBuilder.Typical().Build();

        var signature = Assert.Single(Analyze(signing.Sign(image)).Signatures);

        Assert.True(signature.IsIntact);
        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.Equal(["Bootrix Test Signer", "Bootrix Test CA"], signature.Chain.Select(c => c.CommonName));
    }

    [ToolTheory("openssl", "osslsigncode")]
    [InlineData("sha1", "SHA1")]
    [InlineData("sha256", "SHA256")]
    [InlineData("sha384", "SHA384")]
    [InlineData("sha512", "SHA512")]
    public void Osslsigncode_DigestAlgorithms_AreComparedWithTheirOwnImageHash(string digest, string expected)
    {
        var signature = Assert.Single(Analyze(signing.Sign(PeVariants.Build(PeVariants.TrailingData), digest)).Signatures);

        Assert.Equal(expected, signature.DigestAlgorithm);
        Assert.True(signature.IsIntact);
    }

    [ToolFact("openssl", "osslsigncode")]
    public void Osslsigncode_NestedSignature_IsReadAsSecondSignature()
    {
        var image = PeBuilder.Typical().Build();
        var nested = signing.Sign(signing.Sign(image, "sha1"), "sha256", nest: true);

        var signatures = Analyze(nested).Signatures;

        Assert.Equal(2, signatures.Count);
        Assert.Equal(["SHA1", "SHA256"], signatures.Select(s => s.DigestAlgorithm).Order());
        Assert.Single(signatures, s => s.IsNested);
        Assert.All(signatures, s => Assert.True(s.IsIntact));
    }

    [ToolFact("openssl", "osslsigncode")]
    public void Osslsigncode_SignatureOfALookalikeCa_IsNotMicrosoft()
    {
        var signed = signing.Sign(PeBuilder.Typical().Build(), caName: "Microsoft Corporation UEFI CA 2011");

        Assert.Equal(SignatureAuthority.Other, Assert.Single(Analyze(signed).Signatures).Authority);
    }

    [ToolFact("openssl", "osslsigncode")]
    public void Osslsigncode_WithTheRealMicrosoftCaAddedToTheBlock_IsStillNotMicrosoft()
    {
        var real = new SignedCms();
        real.Decode(Fixtures.Read("ms-uefi-ca-2011.p7"));
        var microsoftCa = real.Certificates.Single(c => c.Subject.Contains("UEFI CA 2011", StringComparison.Ordinal));

        var signed = signing.Sign(PeBuilder.Typical().Build(), extraCertificatePem: microsoftCa.ExportCertificatePem());

        var signature = Assert.Single(Analyze(signed).Signatures);
        Assert.Equal(SignatureAuthority.Other, signature.Authority);
        Assert.True(signature.IsIntact);
    }

    [ToolFact("openssl", "osslsigncode")]
    public void Osslsigncode_ImageChangedAfterSigning_DoesNotMatch()
    {
        var signed = signing.Sign(PeBuilder.Typical().Build());
        signed[0x210] ^= 0x01;

        var signature = Assert.Single(Analyze(signed).Signatures);

        Assert.True(signature.SignatureValid);
        Assert.False(signature.DigestMatchesImage);
    }

    [ToolFact("openssl", "sbsign")]
    public void Sbsign_AdditionalSignature_YieldsTwoCertificateTableEntries()
    {
        var image = PeVariants.Build(PeVariants.TrailingData);
        var input = signing.WriteTemp(image);
        var first = signing.GetIdentity("First CA");
        var second = signing.GetIdentity("Second CA");
        var middle = input + ".1";
        var output = input + ".2";
        ExternalTools.RunChecked("sbsign", "--key", first.Key, "--cert", first.Leaf, "--output", middle, input);
        ExternalTools.RunChecked("sbsign", "--key", second.Key, "--cert", second.Leaf, "--output", output, middle);

        var binary = EfiBinary.Parse(File.ReadAllBytes(output));
        var report = Analyze(File.ReadAllBytes(output));

        Assert.Equal(2, binary.Certificates.Count);
        Assert.Equal(2, report.Signatures.Count);
        Assert.All(report.Signatures, s => Assert.True(s.IsIntact));
    }
}
