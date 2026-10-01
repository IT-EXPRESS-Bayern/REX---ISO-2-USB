// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CA5350 // SHA-1 only identifies certificates by their published thumbprints
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Boot;

public class RevocationDataTests
{
    // Authenticode hashes of two old Ubuntu shims (shim-signed 1.6+0.4-0ubuntu4 and 1.40.3+15+1533136590.3beb971-0ubuntu1).
    // pesign -h printed these values for the real binaries and both are entries of the DBX.
    private const string RevokedShim04 = "DC8AFF7FAA9D1A00A3E32EEFBF899B3059CBB313A48B82FA9C8D931FD58FB69D";
    private const string RevokedShim15 = "007F4C95125713B112093E21663E2D23E3C1AE9CE4B5DE0D58A297332336A2D8";

    private static readonly string[] MicrosoftCaThumbprints =
    [
        "3fb39e2b8bd183bf9e4594e72183ca60afcd4277", // Microsoft Option ROM UEFI CA 2023
        "45a0fa32604773c82433c3b7d59e7466b3ac0c67", // Windows UEFI CA 2023
        "46def63b5ce61cf8ba0de2e6639c1019d0ed14f3", // Microsoft Corporation UEFI CA 2011
        "580a6f4cc4e4b669b9ebdc1b2b3e087b80d0678d", // Microsoft Windows Production PCA 2011
        "b5eeb4a6706048073f0ed296e7f580a790b59eaa", // Microsoft UEFI CA 2023
    ];

    private static RevocationData Parse(string json) => RevocationData.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static void AssertInvalid(string json)
    {
        var ex = Assert.Throws<BootrixException>(() => Parse(json));
        Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
    }

    private static byte[] EmbeddedJson()
    {
        using var stream = typeof(RevocationData).Assembly.GetManifestResourceStream("Bootrix.Core.Boot.revocations.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void Embedded_RecordsItsSources()
    {
        var data = RevocationData.Embedded;

        Assert.Equal(["microsoft/secureboot_objects", "rhboot/shim"], data.Sources.Select(s => s.Repository));
        Assert.All(data.Sources, s => Assert.Equal(40, s.Commit.Length));
        Assert.All(data.Sources, s => Assert.Equal(64, s.Sha256.Length));
        Assert.StartsWith("v", data.Sources[0].Ref, StringComparison.Ordinal);
        Assert.NotNull(data.Retrieved);
        Assert.True(data.Retrieved >= new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Embedded_HoldsTheDbxHashesOfAllArchitectures()
    {
        var images = RevocationData.Embedded.Images;

        Assert.InRange(images.Count, 600, 5000);
        Assert.Contains(images, i => i.Machine == EfiMachine.X64);
        Assert.Contains(images, i => i.Machine == EfiMachine.X86);
        Assert.Contains(images, i => i.Machine == EfiMachine.Arm64);
        Assert.Contains(images, i => i.Machine == EfiMachine.Arm);
        Assert.All(images, i => Assert.Matches("^[0-9A-F]{64}$", i.Hash));
    }

    [Theory]
    [InlineData(RevokedShim04, "shim-0.4-0ubuntu4")]
    [InlineData(RevokedShim15, "shim-15+1533136590.3beb971-0ubuntu1")]
    public void Embedded_ContainsTheHashOfARealRevokedUbuntuShim(string hash, string fileNamePart)
    {
        var image = RevocationData.Embedded.FindImage(hash);

        Assert.NotNull(image);
        Assert.Equal(EfiMachine.X64, image.Machine);
        Assert.Equal("Canonical Ltd", image.Company);
        Assert.Equal(new DateOnly(2021, 4, 1), image.Added);
        Assert.Contains(fileNamePart, image.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void FindImage_IgnoresCase_AndReturnsNullForUnknownHashes()
    {
        var data = RevocationData.Embedded;

        Assert.NotNull(data.FindImage(RevokedShim04.ToLowerInvariant()));
        Assert.Null(data.FindImage(new string('0', 64)));
        Assert.Null(data.FindImage(string.Empty));
    }

    [Fact]
    public void Embedded_EveryHashOfTheSignedAmd64AndArm64DbxUpdatesIsKnown()
    {
        // Two independent files of the same repository: the signed update binaries and the JSON the snapshot is built from.
        var data = RevocationData.Embedded;

        foreach (var (file, machine) in new[] { ("DBXUpdate.amd64.bin", EfiMachine.X64), ("DBXUpdate.arm64.bin", EfiMachine.Arm64) })
        {
            foreach (var list in EfiSignatureDatabase.Parse(Fixtures.Read(file)))
            {
                foreach (var entry in list.Entries)
                {
                    var image = data.FindImage(Convert.ToHexString(entry.Data.Span));
                    Assert.NotNull(image);
                    Assert.Equal(machine, image.Machine);
                }
            }
        }
    }

    [Fact]
    public void Embedded_HoldsTheSbatLevel()
    {
        var level = RevocationData.Embedded.SbatLevel;

        Assert.NotNull(level);
        Assert.Matches("^[0-9]{10}$", level.LevelDate);
        Assert.True(level.MinimumGenerations["shim"] >= 4);
        Assert.True(level.MinimumGenerations["grub"] >= 6);
    }

    [Fact]
    public void Embedded_HoldsTheBootManagerSvn()
    {
        var svn = RevocationData.Embedded.FindSvn(RevocationData.BootmgrSvnGuid);

        Assert.NotNull(svn);
        Assert.Equal("bootmgfw.efi", svn.FileName);
        Assert.True(svn.Minimum >= new SecurityVersion(9, 0));
        Assert.Equal(["bootmgfw.efi", "cdboot.efi", "wdsmgfw.efi"], RevocationData.Embedded.Svns.Select(s => s.FileName).Order());
    }

    [Fact]
    public void Embedded_ListsTheWindowsProductionPca2011AsRevoked()
    {
        var certificate = Assert.Single(RevocationData.Embedded.Certificates);

        Assert.Equal("580a6f4cc4e4b669b9ebdc1b2b3e087b80d0678d", certificate.Sha1Thumbprint);
        Assert.Contains("Microsoft Windows Production PCA 2011", certificate.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_CarriesTheFiveMicrosoftCaCertificates()
    {
        var thumbprints = RevocationData.Embedded.TrustedCertificates
            .Select(der => Convert.ToHexStringLower(SHA1.HashData(der)))
            .Order()
            .ToList();

        Assert.Equal(MicrosoftCaThumbprints, thumbprints);
    }

    [Fact]
    public void FromDbx_RealAmd64Update_YieldsOnlyImageHashes()
    {
        var data = RevocationData.FromDbx(Fixtures.Read("DBXUpdate.amd64.bin"));

        Assert.Equal(443, data.Images.Count);
        Assert.Empty(data.Certificates);
        Assert.Empty(data.Svns);
        Assert.Null(data.SbatLevel);
        Assert.NotNull(data.FindImage(RevokedShim04));
    }

    [Fact]
    public void FromDbx_RealUpdateWithCertificate_YieldsTheRevokedCaAndAnOldSvn()
    {
        var data = RevocationData.FromDbx(Fixtures.Read("DBXUpdate2024.bin"));

        var certificate = Assert.Single(data.Certificates);
        Assert.Equal("580a6f4cc4e4b669b9ebdc1b2b3e087b80d0678d", certificate.Sha1Thumbprint);
        Assert.Contains("Microsoft Windows Production PCA 2011", certificate.Subject, StringComparison.Ordinal);
        Assert.Equal(new SecurityVersion(2, 0), data.FindSvn(RevocationData.BootmgrSvnGuid)?.Minimum);
    }

    [Fact]
    public void FromDbx_RealSvnUpdate_AgreesWithTheEmbeddedSnapshot()
    {
        var fromFile = RevocationData.FromDbx(Fixtures.Read("DBXUpdateSVN.bin"));
        var embedded = RevocationData.Embedded;

        Assert.Empty(fromFile.Images);
        Assert.Equal(3, fromFile.Svns.Count);
        foreach (var svn in fromFile.Svns)
        {
            var other = embedded.FindSvn(svn.Component);
            Assert.NotNull(other);
            Assert.Equal(svn.FileName, other.FileName);
            Assert.True(svn.Minimum <= other.Minimum);
        }
    }

    [Fact]
    public void FromDbx_HashesWithTheSvnOwnerAreVersionsNotImages()
    {
        var dbx = DbxBuilder.List(
            DbxBuilder.Sha256Type,
            [(DbxBuilder.SvnOwner, DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 11, 2)), (DbxBuilder.SomeOwner, new byte[32])]);

        var data = RevocationData.FromDbx(dbx);

        Assert.Equal(new SecurityVersion(11, 2), data.FindSvn(RevocationData.BootmgrSvnGuid)?.Minimum);
        Assert.Single(data.Images);
    }

    [Fact]
    public void FromDbx_SvnEntriesWithBadStructure_AreIgnored()
    {
        var wrongVersionByte = DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 9, 0);
        wrongVersionByte[0] = 2;
        var nonZeroPadding = DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 9, 0);
        nonZeroPadding[30] = 1;

        var data = RevocationData.FromDbx(DbxBuilder.List(DbxBuilder.Sha256Type, [(DbxBuilder.SvnOwner, wrongVersionByte), (DbxBuilder.SvnOwner, nonZeroPadding)]));

        Assert.Empty(data.Svns);
    }

    [Fact]
    public void FromDbx_SeveralSvnEntriesOfOneComponent_KeepTheHighest()
    {
        var dbx = DbxBuilder.List(
            DbxBuilder.Sha256Type,
            [
                (DbxBuilder.SvnOwner, DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 7, 0)),
                (DbxBuilder.SvnOwner, DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 9, 1)),
                (DbxBuilder.SvnOwner, DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 8, 5)),
            ]);

        Assert.Equal(new SecurityVersion(9, 1), RevocationData.FromDbx(dbx).FindSvn(RevocationData.BootmgrSvnGuid)?.Minimum);
    }

    [Fact]
    public void FromDbx_CertificateRevokedByTbsHash_IsFoundInTheChain()
    {
        var tbs = new string('A', 64).ToLowerInvariant();
        var dbx = DbxBuilder.List(DbxBuilder.X509Sha256Type, [(DbxBuilder.SomeOwner, [.. Convert.FromHexString(tbs), .. new byte[16]])]);
        var data = RevocationData.FromDbx(dbx);

        var chain = new[]
        {
            new EfiCertificateInfo("CN=leaf", "leaf", "CN=ca", new string('1', 40), new string('2', 64), new string('3', 64)),
            new EfiCertificateInfo("CN=ca", "ca", "CN=root", new string('4', 40), new string('5', 64), tbs),
        };

        Assert.Equal("ca", data.FindRevokedCertificate(chain)?.CommonName);
        Assert.Null(data.FindRevokedCertificate(chain.Take(1)));
    }

    [Fact]
    public void FromDbx_UnreadableCertificate_IsKeptByItsThumbprint()
    {
        var garbage = PeBuilder.Pattern(300, 5);

        var data = RevocationData.FromDbx(DbxBuilder.List(DbxBuilder.X509Type, [(DbxBuilder.SomeOwner, garbage)]));

        var certificate = Assert.Single(data.Certificates);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(garbage)), certificate.Sha1Thumbprint);
    }

    [Fact]
    public void FromDbx_InvalidContent_Throws()
    {
        var ex = Assert.Throws<BootrixException>(() => RevocationData.FromDbx(new byte[40]));

        Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
    }

    [Fact]
    public void Merge_CombinesHashesCertificatesAndKeepsTheHigherSvn()
    {
        var embedded = RevocationData.Embedded;
        var device = RevocationData.FromDbx(Fixtures.Read("DBXUpdate2024.bin"));
        var extra = RevocationData.FromDbx(DbxBuilder.Sha256List(new string('C', 64)));

        var merged = embedded.Merge(device).Merge(extra);

        Assert.Equal(embedded.Images.Count + 1, merged.Images.Count);
        Assert.NotNull(merged.FindImage(new string('C', 64)));
        Assert.Single(merged.Certificates);
        Assert.Equal(embedded.FindSvn(RevocationData.BootmgrSvnGuid)?.Minimum, merged.FindSvn(RevocationData.BootmgrSvnGuid)?.Minimum);
        Assert.Same(embedded.SbatLevel, merged.SbatLevel);
        Assert.Equal(embedded.Retrieved, merged.Retrieved);
        Assert.Equal(embedded.Sources.Count, merged.Sources.Count);
    }

    [Fact]
    public void Merge_TheLaterSbatLevelWins()
    {
        var newer = Parse("""{"schemaVersion":1,"sbat":{"level":"2030010100","components":{"grub":9}}}""");

        var merged = RevocationData.Embedded.Merge(newer);

        Assert.Equal("2030010100", merged.SbatLevel?.LevelDate);
    }

    [Fact]
    public void Load_MinimalDocument_AppliesEmptyDefaultsInsteadOfFailing()
    {
        var data = Parse("""{"schemaVersion":1,"images":[{"hash":"%s"}]}""".Replace("%s", new string('a', 64), StringComparison.Ordinal));

        var image = Assert.Single(data.Images);
        Assert.Equal(new string('A', 64), image.Hash);
        Assert.Equal(string.Empty, image.FileName);
        Assert.Equal(string.Empty, image.Company);
        Assert.Equal(EfiMachine.Unknown, image.Machine);
        Assert.Null(image.Added);
        Assert.Empty(data.Sources);
        Assert.Empty(data.Svns);
        Assert.Null(data.SbatLevel);
    }

    [Fact]
    public void Load_ReadsEveryPartOfTheFormat()
    {
        var json = """
            {
              "schemaVersion": 1,
              "retrieved": "2026-10-02",
              "sources": [{"repository":"r/x","ref":"v1","commit":"COMMIT","file":"f.json","sha256":"SHA","license":"MIT"}],
              "sbat": {"level":"2026010100","components":{"shim":5}},
              "images": [{"hash":"HASH","machine":"aarch64","file":"x.efi","company":"Acme","added":"2026-01-02","description":"why"}],
              "revokedCertificates": [{"subject":"CN = Old CA","sha1":"THUMB","added":"2026-01-03","description":"gone"}],
              "svns": [{"component":"bootmgfw.efi","guid":"9d132b61-59d5-4388-ab1c-185c3cb2eb92","version":"10.1","changed":"2026-02-02"}],
              "trustedCertificates": [{"name":"x","sha1":"","sha256":"","der":"AQID"}]
            }
            """
            .Replace("COMMIT", new string('a', 40), StringComparison.Ordinal)
            .Replace("SHA", new string('b', 64), StringComparison.Ordinal)
            .Replace("HASH", new string('c', 64), StringComparison.Ordinal)
            .Replace("THUMB", new string('D', 40), StringComparison.Ordinal);

        var data = Parse(json);

        Assert.Equal(new DateOnly(2026, 10, 2), data.Retrieved);
        Assert.Equal("MIT", Assert.Single(data.Sources).License);
        Assert.Equal("2026010100", data.SbatLevel?.LevelDate);
        var image = Assert.Single(data.Images);
        Assert.Equal((EfiMachine.Arm64, "Acme", "why"), (image.Machine, image.Company, image.Description));
        Assert.Equal(new DateOnly(2026, 1, 2), image.Added);
        Assert.Equal(new string('d', 40), Assert.Single(data.Certificates).Sha1Thumbprint);
        Assert.Equal(new SecurityVersion(10, 1), data.FindSvn(RevocationData.BootmgrSvnGuid)?.Minimum);
        Assert.Equal([1, 2, 3], Assert.Single(data.TrustedCertificates));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":2}""")]
    [InlineData("""{"schemaVersion":1,"images":[{"hash":"abc"}]}""")]
    [InlineData("""{"schemaVersion":1,"images":[{}]}""")]
    [InlineData("""{"schemaVersion":1,"images":[{"hash":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"}]}""")]
    [InlineData("""{"schemaVersion":1,"images":[{"hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","added":"yesterday"}]}""")]
    [InlineData("""{"schemaVersion":1,"revokedCertificates":[{"subject":"x","sha1":"12"}]}""")]
    [InlineData("""{"schemaVersion":1,"svns":[{"guid":"nonsense","version":"1.0"}]}""")]
    [InlineData("""{"schemaVersion":1,"svns":[{"guid":"9d132b61-59d5-4388-ab1c-185c3cb2eb92","version":"one"}]}""")]
    [InlineData("""{"schemaVersion":1,"sbat":{"components":{}}}""")]
    [InlineData("""{"schemaVersion":1,"trustedCertificates":[{"der":"%%%"}]}""")]
    [InlineData("""{"schemaVersion":1,"sources":[{"repository":"only"}]}""")]
    public void Load_InvalidDocuments_AreRejected(string json)
    {
        AssertInvalid(json);
    }

    [Fact]
    public void Load_EveryTruncationOfTheEmbeddedSnapshot_FailsCleanly()
    {
        var json = EmbeddedJson();

        for (var length = 0; length < json.Length; length += 997)
        {
            var ex = Assert.Throws<BootrixException>(() => RevocationData.Load(new MemoryStream(json, 0, length)));
            Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
        }
    }

    [Fact]
    public void Load_EmbeddedSnapshot_RoundTripsThroughTheLoader()
    {
        var loaded = RevocationData.Load(new MemoryStream(EmbeddedJson()));

        Assert.Equal(RevocationData.Embedded.Images.Count, loaded.Images.Count);
        Assert.Equal(RevocationData.Embedded.Retrieved, loaded.Retrieved);
        Assert.Equal(RevocationData.Embedded.SbatLevel?.LevelDate, loaded.SbatLevel?.LevelDate);
    }

    [Fact]
    public void Load_DatesAreCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            var data = Parse("""{"schemaVersion":1,"retrieved":"2026-10-02"}""");

            Assert.Equal(new DateOnly(2026, 10, 2), data.Retrieved);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
