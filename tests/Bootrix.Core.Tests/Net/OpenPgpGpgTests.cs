// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Net;

/// <summary>Keys and signatures made by gpg itself; the verifier must agree with it, including on what to reject.</summary>
public sealed class GpgKeys : IDisposable
{
    public GpgKeys()
    {
        if (!GpgHome.Available)
        {
            return;
        }

        Home = new GpgHome();
        Rsa = Home.GenerateKey("Rsa Signer");
        Ed25519 = Home.GenerateKey("Ed Signer", "ed25519");
        Expiring = Home.GenerateKey("Short Lived", "rsa2048", "2d");
        Weak = Home.GenerateKey("Weak Rsa", "rsa1024");
        Revoked = Home.GenerateKey("Revoked Key");
        WithSubkey = Home.GenerateKey("Subkey Owner");
        Subkey = Home.AddSigningSubkey(WithSubkey);

        // The signature predates the revocation, so only the revocation can make it fail.
        RevokedSignature = Home.DetachSign(Data, Revoked);
        KeyringBeforeRevocation = Home.ExportPublic(Revoked);
        RevokedKeyring = Home.ExportRevoked(Revoked);
    }

    public static byte[] Data { get; } = Encoding.UTF8.GetBytes("a1b2  image.iso\n");

    public GpgHome Home { get; } = null!;

    public string Rsa { get; } = string.Empty;

    public string Ed25519 { get; } = string.Empty;

    public string Expiring { get; } = string.Empty;

    public string Weak { get; } = string.Empty;

    public string Revoked { get; } = string.Empty;

    public string WithSubkey { get; } = string.Empty;

    public string Subkey { get; } = string.Empty;

    public byte[] RevokedSignature { get; } = [];

    public byte[] RevokedKeyring { get; } = [];

    public byte[] KeyringBeforeRevocation { get; } = [];

    public OpenPgpKeyring Keyring(params string[] fingerprints) =>
        OpenPgpKeyring.Load(fingerprints.SelectMany(f => Home.ExportPublic(f)).ToArray());

    public void Dispose() => Home?.Dispose();
}

public class OpenPgpGpgTests(GpgKeys keys) : IClassFixture<GpgKeys>
{
    private static string KeyIdOf(string fingerprint) => fingerprint[^16..];

    [GpgFact]
    public void RsaSignatureArmoredAndBinary()
    {
        var keyring = keys.Keyring(keys.Rsa);

        foreach (var armor in new[] { true, false })
        {
            var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa, armor);
            var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keyring);

            Assert.Equal(OpenPgpStatus.Valid, result.Status);
            Assert.Equal(keys.Rsa, result.Fingerprint);
        }
    }

    [GpgFact]
    public void Ed25519Signature()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Ed25519);

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Ed25519));

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
        Assert.Equal(keys.Ed25519, result.Fingerprint);
    }

    [GpgFact]
    public void BinaryKeyringIsUnderstood()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa);

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Home.ExportPublic(keys.Rsa, armor: false));

        Assert.True(result.IsValid);
    }

    [GpgFact]
    public void ChangedDataIsInvalid()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa);
        var forged = (byte[])GpgKeys.Data.Clone();
        forged[0] ^= 1;

        var result = OpenPgpVerifier.Verify(forged, signature, keys.Keyring(keys.Rsa));

        Assert.Equal(OpenPgpStatus.Invalid, result.Status);
    }

    [GpgFact]
    public void SignatureOfAnotherKeyIsUnknown()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa);

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Ed25519));

        Assert.Equal(OpenPgpStatus.KeyUnknown, result.Status);
        Assert.Equal(KeyIdOf(keys.Rsa), result.KeyId);
    }

    [GpgFact]
    public void ExpiredKeyIsRejectedOnlyAfterItExpires()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Expiring);
        var keyring = keys.Keyring(keys.Expiring);

        var now = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keyring);
        var later = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keyring, time: new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(10)));

        Assert.Equal(OpenPgpStatus.Valid, now.Status);
        Assert.Equal(OpenPgpStatus.KeyExpired, later.Status);
        Assert.Equal(keys.Expiring, later.Fingerprint);
    }

    [GpgFact]
    public void RevokedKeyIsRejected()
    {
        var result = OpenPgpVerifier.Verify(GpgKeys.Data, keys.RevokedSignature, OpenPgpKeyring.Load(keys.RevokedKeyring));

        Assert.Equal(OpenPgpStatus.KeyRevoked, result.Status);
        Assert.Equal(keys.Revoked, result.Fingerprint);
    }

    [GpgFact]
    public void SameKeyBeforeTheRevocationStillVerifies()
    {
        var result = OpenPgpVerifier.Verify(GpgKeys.Data, keys.RevokedSignature, keys.KeyringBeforeRevocation);

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
    }

    [GpgFact]
    public void RevocationInAnyCopyOfTheKeyCounts()
    {
        var both = OpenPgpKeyring.Load([.. keys.KeyringBeforeRevocation, .. keys.RevokedKeyring]);

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, keys.RevokedSignature, both);

        Assert.Equal(OpenPgpStatus.KeyRevoked, result.Status);
    }

    [GpgFact]
    public void SubkeySignatureIsAttributedToThePrimaryKey()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Subkey + "!");

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.WithSubkey));

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
        Assert.Equal(keys.WithSubkey, result.Fingerprint);
        Assert.Equal(KeyIdOf(keys.Subkey), result.KeyId);
    }

    [GpgFact]
    public void SubkeyFingerprintCanBePinnedToo()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Subkey + "!");

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.WithSubkey), [keys.Subkey]);

        Assert.True(result.IsValid);
    }

    [GpgFact]
    public void PinningAnotherFingerprintBlocksAnOtherwiseValidSignature()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa);

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Rsa, keys.Ed25519), [keys.Ed25519]);

        Assert.Equal(OpenPgpStatus.KeyNotAllowed, result.Status);
    }

    [GpgFact]
    public void AnyKnownKeyAmongSeveralSignaturesIsEnough()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa)
            .Concat(keys.Home.DetachSign(GpgKeys.Data, keys.Ed25519))
            .ToArray();

        var onlySecond = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Ed25519));
        var neither = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Expiring));

        Assert.Equal(keys.Ed25519, onlySecond.Fingerprint);
        Assert.True(onlySecond.IsValid);
        Assert.Equal(OpenPgpStatus.KeyUnknown, neither.Status);
    }

    [GpgFact]
    public void Sha1SignaturesAreNotAccepted()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Rsa, digest: "SHA1");

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Rsa));

        Assert.Equal(OpenPgpStatus.Invalid, result.Status);
        Assert.Contains("Sha1", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [GpgFact]
    public void ShortRsaKeysAreNotAccepted()
    {
        var signature = keys.Home.DetachSign(GpgKeys.Data, keys.Weak);

        var result = OpenPgpVerifier.Verify(GpgKeys.Data, signature, keys.Keyring(keys.Weak));

        Assert.Equal(OpenPgpStatus.Invalid, result.Status);
        Assert.Contains("1024", result.Reason, StringComparison.Ordinal);
    }

    [GpgFact]
    public void ClearSignedTextComesBackWithoutArmorAndEscapes()
    {
        const string text = "line one\n- not a list\n----- dashes\n\ntrailing blanks   \nlast\n";
        var document = keys.Home.ClearSign(text, keys.Rsa);

        var result = OpenPgpVerifier.VerifyClearSigned(document, keys.Keyring(keys.Rsa));

        Assert.Equal(OpenPgpStatus.Valid, result.Result.Status);
        Assert.Equal(text, result.Text);
        Assert.Contains("- - not a list", Encoding.UTF8.GetString(document), StringComparison.Ordinal);
    }

    [GpgFact]
    public void ClearSignedTextWithTabsAndSpacesAtLineEndsIsStillCoveredByTheSignature()
    {
        const string text = "a \t\nb\t\n\nc\n";
        var document = keys.Home.ClearSign(text, keys.Ed25519);

        var result = OpenPgpVerifier.VerifyClearSigned(document, keys.Keyring(keys.Ed25519));

        Assert.True(result.Result.IsValid);
    }

    [GpgFact]
    public void ClearSignedDocumentWithWindowsLineEndingsVerifies()
    {
        var document = keys.Home.ClearSign("one\ntwo\n", keys.Rsa);
        var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(document).Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = OpenPgpVerifier.VerifyClearSigned(crlf, keys.Keyring(keys.Rsa));

        Assert.True(result.Result.IsValid);
        Assert.Equal("one\ntwo\n", result.Text);
    }

    [GpgFact]
    public void TamperedClearSignedTextIsInvalid()
    {
        var document = Encoding.UTF8.GetString(keys.Home.ClearSign("one\ntwo\n", keys.Rsa)).Replace("two", "too", StringComparison.Ordinal);

        var result = OpenPgpVerifier.VerifyClearSigned(Encoding.UTF8.GetBytes(document), keys.Keyring(keys.Rsa));

        Assert.Equal(OpenPgpStatus.Invalid, result.Result.Status);
        Assert.Equal(string.Empty, result.Text);
    }

    [GpgFact]
    public void ExtraLineInsertedIntoTheSignedTextIsInvalid()
    {
        var document = Encoding.UTF8.GetString(keys.Home.ClearSign("one\ntwo\n", keys.Rsa)).Replace("one\n", "one\nextra\n", StringComparison.Ordinal);

        var result = OpenPgpVerifier.VerifyClearSigned(Encoding.UTF8.GetBytes(document), keys.Keyring(keys.Rsa));

        Assert.Equal(OpenPgpStatus.Invalid, result.Result.Status);
    }

    [GpgFact]
    public void ClearSignedDocumentWithAnUnescapedArmorLineIsRejected()
    {
        var document = Encoding.UTF8.GetString(keys.Home.ClearSign("one\n", keys.Rsa))
            .Replace("one\n", "one\n-----BEGIN PGP SIGNED MESSAGE-----\n", StringComparison.Ordinal);

        var result = OpenPgpVerifier.VerifyClearSigned(Encoding.UTF8.GetBytes(document), keys.Keyring(keys.Rsa));

        Assert.Equal(OpenPgpStatus.Invalid, result.Result.Status);
    }
}
