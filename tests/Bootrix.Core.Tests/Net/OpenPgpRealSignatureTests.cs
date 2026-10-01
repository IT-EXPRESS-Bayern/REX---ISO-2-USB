// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Net;

/// <summary>
/// Signatures made by Ubuntu, Debian and Fedora on their published checksum files, checked offline against their
/// public keys. The files were downloaded from the distributions' servers; gpg verifies each of them as well.
/// </summary>
public class OpenPgpRealSignatureTests
{
    // Ubuntu CD Image Automatic Signing Key (2012), from ubuntu-keyring.
    private const string UbuntuCd = "843938DF228D22F7B3742BC0D94AA3F0EFE21092";

    // Debian CD signing key; the fingerprint is the one printed on https://www.debian.org/CD/verify.
    private const string DebianCd = "DF9B9C49EAA9298432589D76DA87E80D6294BE9B";

    // Fedora (44) primary key, one of four in fedora.gpg.
    private const string Fedora44 = "36F612DCF27F7D1A48A835E4DBFCF71C6D9F90A6";

    private static OpenPgpKeyring Keyring(string fixture) => OpenPgpKeyring.Load(NetFixtures.Bytes(fixture));

    [Fact]
    public void UbuntuChecksumFileWithArmoredDetachedSignature()
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("ubuntu/SHA256SUMS"),
            NetFixtures.Bytes("ubuntu/SHA256SUMS.gpg"),
            Keyring("ubuntu/ubuntu-cd-signing-key.asc"));

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
        Assert.Equal(UbuntuCd, result.Fingerprint);
        Assert.Equal("D94AA3F0EFE21092", result.KeyId);
    }

    [Fact]
    public void DebianChecksumFileWithDetachedSignature()
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            NetFixtures.Bytes("debian/SHA256SUMS.sign"),
            Keyring("debian/debian-cd-signing-key.asc"));

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
        Assert.Equal(DebianCd, result.Fingerprint);
    }

    [Fact]
    public void FedoraClearSignedChecksumAgainstABinaryKeyringWithSeveralKeys()
    {
        var keyring = Keyring("fedora/fedora.gpg");

        var result = OpenPgpVerifier.VerifyClearSigned(NetFixtures.Bytes("fedora/CHECKSUM"), keyring);

        Assert.Equal(4, keyring.Fingerprints.Count);
        Assert.Equal(OpenPgpStatus.Valid, result.Result.Status);
        Assert.Equal(Fedora44, result.Result.Fingerprint);
        Assert.Contains("SHA256 (Fedora-Workstation-Live-44-1.7.x86_64.iso) = 1620295f", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PGP", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedClearSignedTextFeedsTheChecksumParser()
    {
        var verified = OpenPgpVerifier.VerifyClearSigned(NetFixtures.Bytes("fedora/CHECKSUM"), Keyring("fedora/fedora.gpg"), [Fedora44]);

        verified.Result.ThrowIfNotValid("CHECKSUM");
        var hash = ChecksumFile.Parse(verified.Text).Select("Fedora-Workstation-Live-44-1.7.x86_64.iso");

        Assert.Equal("1620295f6a00c27c3208f0c00b8ece4eab1ec69b9002152d97488bf26a426ddf", hash.Hex);
    }

    [Fact]
    public void ChangedChecksumLineIsDetected()
    {
        var data = NetFixtures.Bytes("ubuntu/SHA256SUMS");
        var forged = (byte[])data.Clone();
        forged[10] = (byte)(forged[10] == (byte)'0' ? '1' : '0');

        var result = OpenPgpVerifier.Verify(forged, NetFixtures.Bytes("ubuntu/SHA256SUMS.gpg"), Keyring("ubuntu/ubuntu-cd-signing-key.asc"));

        Assert.Equal(OpenPgpStatus.Invalid, result.Status);
        Assert.Equal(UbuntuCd, result.Fingerprint);
    }

    [Fact]
    public void ChangedClearSignedTextIsDetectedAndNoTextIsReturned()
    {
        var text = System.Text.Encoding.UTF8.GetString(NetFixtures.Bytes("fedora/CHECKSUM")).Replace("= 1620295f", "= 0620295f", StringComparison.Ordinal);

        var result = OpenPgpVerifier.VerifyClearSigned(System.Text.Encoding.UTF8.GetBytes(text), Keyring("fedora/fedora.gpg"));

        Assert.Equal(OpenPgpStatus.Invalid, result.Result.Status);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void SignatureOfAnotherDistributionIsKeyUnknown()
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            NetFixtures.Bytes("debian/SHA256SUMS.sign"),
            Keyring("ubuntu/ubuntu-cd-signing-key.asc"));

        Assert.Equal(OpenPgpStatus.KeyUnknown, result.Status);
        Assert.Null(result.Fingerprint);
        Assert.Equal("DA87E80D6294BE9B", result.KeyId);
    }

    [Fact]
    public void SignatureOfADifferentFileWithTheRightKeyIsInvalid()
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            NetFixtures.Bytes("debian/SHA256SUMS.sign").AsSpan(..^3),
            Keyring("debian/debian-cd-signing-key.asc"));

        Assert.Equal(OpenPgpStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData("DF9B9C49EAA9298432589D76DA87E80D6294BE9B")]
    [InlineData("DF9B 9C49 EAA9 2984 3258  9D76 DA87 E80D 6294 BE9B")]
    [InlineData("0xdf9b9c49eaa9298432589d76da87e80d6294be9b")]
    public void PinnedFingerprintIsAcceptedInEveryCommonNotation(string pinned)
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            NetFixtures.Bytes("debian/SHA256SUMS.sign"),
            Keyring("debian/debian-cd-signing-key.asc"),
            [pinned]);

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
    }

    [Fact]
    public void ValidSignatureFromAKeyThatIsNotPinnedIsNotAccepted()
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            NetFixtures.Bytes("debian/SHA256SUMS.sign"),
            Keyring("debian/debian-cd-signing-key.asc"),
            [UbuntuCd, Fedora44]);

        Assert.Equal(OpenPgpStatus.KeyNotAllowed, result.Status);
        Assert.Equal(DebianCd, result.Fingerprint);
    }

    [Fact]
    public void KeyringsFromSeveralDownloadsCanBeLoadedFromOneArmoredFile()
    {
        var combined = NetFixtures.Bytes("ubuntu/ubuntu-cd-signing-key.asc")
            .Concat(NetFixtures.Bytes("debian/debian-cd-signing-key.asc"))
            .ToArray();

        var keyring = OpenPgpKeyring.Load(combined);

        Assert.Equal([UbuntuCd, DebianCd], keyring.Fingerprints);
        Assert.True(OpenPgpVerifier.Verify(NetFixtures.Bytes("debian/SHA256SUMS"), NetFixtures.Bytes("debian/SHA256SUMS.sign"), keyring).IsValid);
        Assert.True(OpenPgpVerifier.Verify(NetFixtures.Bytes("ubuntu/SHA256SUMS"), NetFixtures.Bytes("ubuntu/SHA256SUMS.gpg"), keyring).IsValid);
    }

    [Fact]
    public void ThrowIfNotValidRaisesSignatureInvalid()
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            NetFixtures.Bytes("debian/SHA256SUMS.sign"),
            Keyring("ubuntu/ubuntu-cd-signing-key.asc"));

        var ex = Assert.Throws<Core.Errors.BootrixException>(() => result.ThrowIfNotValid("SHA256SUMS"));

        Assert.Equal(Core.Errors.ErrorCode.SignatureInvalid, ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a keyring")]
    [InlineData("-----BEGIN PGP PUBLIC KEY BLOCK-----\n\nAAAA\n-----END PGP PUBLIC KEY BLOCK-----\n")]
    public void UnusableKeyringIsRejectedWithAClearException(string content)
    {
        Assert.Throws<InvalidDataException>(() => OpenPgpKeyring.Load(System.Text.Encoding.ASCII.GetBytes(content)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is no signature")]
    [InlineData("-----BEGIN PGP SIGNATURE-----\n\nAAAA\n-----END PGP SIGNATURE-----\n")]
    public void UnusableSignatureIsInvalidAndDoesNotThrow(string content)
    {
        var result = OpenPgpVerifier.Verify(
            NetFixtures.Bytes("debian/SHA256SUMS"),
            System.Text.Encoding.ASCII.GetBytes(content),
            Keyring("debian/debian-cd-signing-key.asc"));

        Assert.Equal(OpenPgpStatus.Invalid, result.Status);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\nhello\n")]
    public void MalformedClearSignedDocumentIsInvalid(string content)
    {
        var result = OpenPgpVerifier.VerifyClearSigned(System.Text.Encoding.ASCII.GetBytes(content), Keyring("fedora/fedora.gpg"));

        Assert.Equal(OpenPgpStatus.Invalid, result.Result.Status);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void TextBeforeOrAfterTheClearSignedBlockIsRejected()
    {
        var original = System.Text.Encoding.UTF8.GetString(NetFixtures.Bytes("fedora/CHECKSUM"));
        var keyring = Keyring("fedora/fedora.gpg");

        var before = OpenPgpVerifier.VerifyClearSigned(System.Text.Encoding.UTF8.GetBytes("SHA256 (evil.iso) = " + new string('0', 64) + "\n" + original), keyring);
        var after = OpenPgpVerifier.VerifyClearSigned(System.Text.Encoding.UTF8.GetBytes(original + "SHA256 (evil.iso) = " + new string('0', 64) + "\n"), keyring);

        Assert.Equal(OpenPgpStatus.Invalid, before.Result.Status);
        Assert.Equal(OpenPgpStatus.Invalid, after.Result.Status);
    }
}
