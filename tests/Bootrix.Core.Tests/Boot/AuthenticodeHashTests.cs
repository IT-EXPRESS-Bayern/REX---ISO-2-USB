// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bootrix.Core.Boot;

namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// The Authenticode hash is checked against pesign and osslsigncode, which are independent implementations of the
/// PE/COFF specification. Tests that need a tool are skipped when it is missing; the hashes pesign produced for the
/// deterministic images of <see cref="PeVariants"/> are kept as constants so the algorithm stays pinned on such machines.
/// </summary>
public partial class AuthenticodeHashTests(SigningFixture signing) : IClassFixture<SigningFixture>
{
    // pesign hashes images in section table order; osslsigncode, EDK2 and the specification sort by file offset.
    // Real linkers write both in the same order, so only the artificial SectionsOutOfOrder image tells them apart.
    public static TheoryData<string> PesignComparable =>
    [
        PeVariants.Typical,
        PeVariants.Pe32,
        PeVariants.TrailingData,
        PeVariants.Checksum,
        PeVariants.EmptySection,
        PeVariants.ManySections,
        PeVariants.WithResources,
        PeVariants.LargeAlignment,
    ];

    public static TheoryData<string> Signable =>
    [
        .. PesignComparable,
        PeVariants.SectionsOutOfOrder,
    ];

    private static string ComputeHex(byte[] image, string algorithm = "SHA256") =>
        Convert.ToHexStringLower(EfiBinary.Parse(image).ComputeAuthenticodeHash(new HashAlgorithmName(algorithm)));

    private static string Pesign(string path) => ExternalTools.RunChecked("pesign", "-h", "-i", path).StdOut.Split(' ')[0].Trim();

    [GeneratedRegex(@"Calculated message digest\s*:\s*([0-9A-Fa-f]+)")]
    private static partial Regex CalculatedDigest();

    private static string OsslsigncodeDigest(string path) =>
        CalculatedDigest().Match(ExternalTools.Run("osslsigncode", ["verify", "-in", path]).Output).Groups[1].Value.ToLowerInvariant();

    [Theory]
    [InlineData(PeVariants.Typical, "68f0431188be4770dddf10032f56fcbe1ab772c896635f552f8f7c767e9d29f3")]
    [InlineData(PeVariants.Pe32, "6ae7b741b85e2c5fcecdb22c529eb8e04932cbdd00e531e3d0f19d4cae674d83")]
    [InlineData(PeVariants.TrailingData, "b031f36e8b12efb6bec4c1a1358b4db8c88876912b61db4afd91d4de958fb28c")]
    [InlineData(PeVariants.Checksum, "258a4222a531f8dbbe624542e8a89ef74a634f7de7c8d49d872c39bdb4b24ae6")]
    [InlineData(PeVariants.EmptySection, "601ea6aadd3d130a41936c8b651596134876273fcad1e750db340b4724df2aaa")]
    [InlineData(PeVariants.ManySections, "800c8bd2eb7846be8b3d4d1645b0b2f944e1b3d32e5141ced145809c3913bd77")]
    [InlineData(PeVariants.WithResources, "52e3d7538bc3415fea37e29b5707351a81141b11a61ab27369c609d95120d244")]
    [InlineData(PeVariants.LargeAlignment, "a35cf04bb915b53d013176d9134fe1fda48b3e72a7c3328a58984999b8b929e5")]
    public void KnownAnswer_EqualsWhatPesignProduced(string variant, string expected)
    {
        Assert.Equal(expected, ComputeHex(PeVariants.Build(variant)));
    }

    [ToolTheory("pesign")]
    [MemberData(nameof(PesignComparable))]
    public void UnsignedImage_MatchesPesign(string variant)
    {
        var image = PeVariants.Build(variant);

        Assert.Equal(Pesign(signing.WriteTemp(image)), ComputeHex(image));
    }

    [ToolTheory("pesign", "osslsigncode", "openssl")]
    [MemberData(nameof(Signable))]
    public void SignedImage_KeepsTheHashAndMatchesTheTools(string variant)
    {
        var unsigned = PeVariants.Build(variant);
        var signed = signing.Sign(unsigned);
        var path = signing.WriteTemp(signed);

        var ours = ComputeHex(signed);

        Assert.Equal(ComputeHex(unsigned), ours);
        Assert.Equal(OsslsigncodeDigest(path), ours);
        if (variant != PeVariants.SectionsOutOfOrder)
        {
            Assert.Equal(Pesign(path), ours);
        }
    }

    [ToolFact("osslsigncode", "openssl")]
    public void SectionsInFileOrderNotTableOrder_FollowTheSpecification()
    {
        var image = PeVariants.Build(PeVariants.SectionsOutOfOrder);
        var path = signing.WriteTemp(signing.Sign(image));

        var tableOrder = new PeBuilder().AddSection(".text", PeBuilder.Pattern(0x300, 1)).AddSection(".data", PeBuilder.Pattern(0x150, 2)).Build();

        Assert.Equal(OsslsigncodeDigest(path), ComputeHex(image));
        Assert.NotEqual(ComputeHex(tableOrder), ComputeHex(image));
    }

    [ToolTheory("osslsigncode", "openssl")]
    [InlineData("sha1")]
    [InlineData("sha384")]
    [InlineData("sha512")]
    public void OtherDigestAlgorithms_MatchTheSignatureDigest(string digest)
    {
        var signed = signing.Sign(PeVariants.Build(PeVariants.TrailingData), digest);

        Assert.Equal(OsslsigncodeDigest(signing.WriteTemp(signed)), ComputeHex(signed, digest.ToUpperInvariant()));
    }

    [ToolFact("sbsign", "sbverify", "pesign", "openssl", "osslsigncode")]
    public void ImageSignedBySbsign_HasTheSameHashAsBefore()
    {
        var identity = signing.GetIdentity();
        var image = PeVariants.Build(PeVariants.TrailingData);
        var input = signing.WriteTemp(image);
        var output = input + ".sb";
        ExternalTools.RunChecked("sbsign", "--key", identity.Key, "--cert", identity.Leaf, "--output", output, input);

        var signed = File.ReadAllBytes(output);

        Assert.True(EfiBinary.Parse(signed).IsSigned);
        Assert.Equal(ComputeHex(image), ComputeHex(signed));
        Assert.Equal(Pesign(output), ComputeHex(signed));
        ExternalTools.RunChecked("sbverify", "--cert", identity.Leaf, output);
    }

    [Fact]
    public void ImageWithoutSecurityDirectoryEntry_HashesEverythingExceptTheChecksum()
    {
        // EDK2 only skips the directory entry when the optional header has one; this follows it.
        var image = PeVariants.Build(PeVariants.NoSecurityEntry);
        var checksumField = 0x98 + 64;

        var expected = Convert.ToHexStringLower(SHA256.HashData([.. image.AsSpan(0, checksumField), .. image.AsSpan(checksumField + 4)]));

        Assert.Equal(expected, ComputeHex(image));
    }

    [Fact]
    public void UnsignedImage_HashIncludesPaddingToEightBytes()
    {
        var image = PeVariants.Build(PeVariants.TrailingData);
        Assert.NotEqual(0, image.Length % 8);
        var padded = new byte[(image.Length + 7) / 8 * 8];
        image.CopyTo(padded, 0);

        // The padded file has a section raw size that is unchanged; only the trailing region grew by the padding.
        var reference = new PeBuilder { TrailingData = [.. PeBuilder.Pattern(100, 7), .. new byte[4]] }
            .AddSection(".text", PeBuilder.Pattern(0x300, 1))
            .AddSection(".data", PeBuilder.Pattern(0x150, 2))
            .Build();

        Assert.Equal(ComputeHex(reference), ComputeHex(image));
    }

    [Fact]
    public void SignatureBlockChangesNeitherHashNorSectionData()
    {
        var unsigned = PeVariants.Build(PeVariants.TrailingData);
        var signedLike = new PeBuilder { TrailingData = PeBuilder.Pattern(100, 7) }
            .AddSection(".text", PeBuilder.Pattern(0x300, 1))
            .AddSection(".data", PeBuilder.Pattern(0x150, 2))
            .AddCertificate(PeBuilder.Pattern(500, 9))
            .Build();

        Assert.True(EfiBinary.Parse(signedLike).IsSigned);
        Assert.Equal(ComputeHex(unsigned), ComputeHex(signedLike));
    }

    [Fact]
    public void ChangingASectionByteChangesTheHash()
    {
        var image = PeVariants.Build(PeVariants.Typical);
        var original = ComputeHex(image);

        image[0x210] ^= 1;

        Assert.NotEqual(original, ComputeHex(image));
    }

    [Fact]
    public void ChangingTheChecksumOrTheSecurityEntryDoesNotChangeTheHash()
    {
        var image = PeVariants.Build(PeVariants.Typical);
        var original = ComputeHex(image);

        image[0x98 + 64] ^= 0xFF; // CheckSum
        image[0x98 + 112 + 4 * 8] = 0x55; // security directory entry

        Assert.Equal(original, ComputeHex(image));
    }

    [Fact]
    public void ChangingAHeaderByteChangesTheHash()
    {
        var image = PeVariants.Build(PeVariants.Typical);
        var original = ComputeHex(image);

        image[0x98 + 68] ^= 1; // Subsystem

        Assert.NotEqual(original, ComputeHex(image));
    }

    [Fact]
    public void Cancellation_StopsTheComputation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var binary = EfiBinary.Parse(PeVariants.Build(PeVariants.Typical));

        Assert.Throws<OperationCanceledException>(() => binary.ComputeAuthenticodeHash(HashAlgorithmName.SHA256, cts.Token));
    }

    /// <summary>
    /// Real boot loaders from Debian, Ubuntu, AlmaLinux and an old Windows boot manager. The constants are what pesign
    /// printed; the two oldest Ubuntu shims are also entries of the DBX (see RevocationDataTests).
    /// </summary>
    [RealSampleFact]
    public void RealBootLoaders_MatchPesign()
    {
        var known = new (string File, string Sha256)[]
        {
            ("bootmgfw-windows-old-x64.efi", "8174748784a7aba345ac118654177831e12fb563b4d8cf0cca937a269f65f6bf"),
            ("fallback-ubuntu-15.8-x64.efi", "20568f3d34e1c9b06e6ba29f437ad3c8308dfba4a0c2b350ebdca890b27a9628"),
            ("grub-almalinux-2.12-x64.efi", "d42522997855054654cd74ea17591faccf913d617a82077aeedabae804821268"),
            ("grub-ubuntu-2.12-x64.efi", "4caffb530989433f184353c48d8150fcdce6037933ab129249f50f0068cf1815"),
            ("mokmanager-ubuntu-15.8-x64.efi", "8ab3adff99fcbc6c11f26884075feb0fe7a9f443e2adc665f720b21f13611980"),
            ("shim-almalinux-16.1-aa64.efi", "ceb11717432284c2dccf6b94721abe74c52cbdab86b1b766157f708c18262a5b"),
            ("shim-almalinux-16.1-x64.efi", "c84e8605b1b2f69d7f09dbc09aebffba0fa4beb1a50f87f60b08876be05cd884"),
            ("shim-debian-16.1-x64.efi", "9e9a31db05a5df7f3ac1def4633eae26fb2ead788c0642fddcc1030676a2b287"),
            ("shim-ubuntu-0.4-x64.efi", "dc8aff7faa9d1a00a3e32eefbf899b3059cbb313a48b82fa9c8d931fd58fb69d"),
            ("shim-ubuntu-15.0-x64.efi", "007f4c95125713b112093e21663e2d23e3c1ae9ce4b5de0d58a297332336a2d8"),
            ("shim-ubuntu-15.4-x64.efi", "dbffd70a2c43fd2c1931f18b8f8c08c5181db15f996f747dfed34def52fad036"),
            ("shim-ubuntu-15.8-x64.efi", "724de6844dd0fe618ba5776c7bca0728be38a6544e24e44ef259b987b7abce80"),
        };

        var tested = 0;
        foreach (var (file, sha256) in known.Where(k => RealSamples.Exists(k.File)))
        {
            Assert.Equal(sha256, ComputeHex(File.ReadAllBytes(RealSamples.Path(file))));
            tested++;
        }

        Assert.True(tested > 0, "the sample directory holds none of the expected files");
    }
}

/// <summary>Deterministic images that exercise the corners of the hashing rules.</summary>
internal static class PeVariants
{
    public const string Typical = nameof(Typical);
    public const string Pe32 = nameof(Pe32);
    public const string SectionsOutOfOrder = nameof(SectionsOutOfOrder);
    public const string TrailingData = nameof(TrailingData);
    public const string Checksum = nameof(Checksum);
    public const string EmptySection = nameof(EmptySection);
    public const string ManySections = nameof(ManySections);
    public const string WithResources = nameof(WithResources);
    public const string NoSecurityEntry = nameof(NoSecurityEntry);
    public const string LargeAlignment = nameof(LargeAlignment);

    public static byte[] Build(string name) => name switch
    {
        Typical => PeBuilder.Typical().Build(),
        Pe32 => new PeBuilder { Pe32Plus = false, Machine = 0x14C }
            .AddSection(".text", PeBuilder.Pattern(0x2A0, 3))
            .AddSection(".data", PeBuilder.Pattern(0x90, 4))
            .Build(),
        SectionsOutOfOrder => new PeBuilder { FileOrder = [".data", ".text"] }
            .AddSection(".text", PeBuilder.Pattern(0x300, 1))
            .AddSection(".data", PeBuilder.Pattern(0x150, 2))
            .Build(),
        TrailingData => new PeBuilder { TrailingData = PeBuilder.Pattern(100, 7) }
            .AddSection(".text", PeBuilder.Pattern(0x300, 1))
            .AddSection(".data", PeBuilder.Pattern(0x150, 2))
            .Build(),
        Checksum => new PeBuilder { CheckSum = 0xDEADBEEF }
            .AddSection(".text", PeBuilder.Pattern(0x300, 1))
            .Build(),
        EmptySection => new PeBuilder()
            .AddSection(".text", PeBuilder.Pattern(0x200, 1))
            .AddSection(".bss", [], virtualSize: 0x3000)
            .AddSection(".data", PeBuilder.Pattern(0x80, 2))
            .Build(),
        ManySections => ManySectionsImage(),
        WithResources => PeBuilder.Typical().AddBootmgrSecurityVersion(9, 0).Build(),
        NoSecurityEntry => new PeBuilder { NumberOfRvaAndSizes = 4 }
            .AddSection(".text", PeBuilder.Pattern(0x300, 1))
            .Build(),
        LargeAlignment => new PeBuilder { FileAlignment = 0x1000 }
            .AddSection(".text", PeBuilder.Pattern(0x1200, 1))
            .AddSection(".data", PeBuilder.Pattern(0x10, 2))
            .Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] ManySectionsImage()
    {
        var builder = new PeBuilder();
        for (var i = 0; i < 12; i++)
        {
            builder.AddSection(".s" + i, PeBuilder.Pattern(0x40 + i * 0x31, i));
        }

        return builder.Build();
    }
}
