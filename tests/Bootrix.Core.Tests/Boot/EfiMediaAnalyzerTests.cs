// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Bootrix.Core.Boot;
using Bootrix.Core.Localization;
using Microsoft.Extensions.Logging;
using static Bootrix.Core.Tests.Boot.CompatibilityMatrixTests;

namespace Bootrix.Core.Tests.Boot;

public class EfiMediaAnalyzerTests(AuthorityFixture authorities) : IClassFixture<AuthorityFixture>
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en") };
    private static readonly Localizer German = new() { Culture = CultureInfo.GetCultureInfo("de") };

    private static EfiAnalysisReport Run(RevocationData? data, params (string Path, byte[] Image)[] files) =>
        new EfiMediaAnalyzer(data).Analyze(files.Select(f => (f.Path, (Stream)new MemoryStream(f.Image))));

    private static EfiAnalysisReport Run(params (string Path, byte[] Image)[] files) => Run(null, files);

    private byte[] Signed(Func<PeBuilder> make, string digest = "SHA256")
    {
        var block = authorities.Own.Sign(make().Build(), digest);
        return make().AddCertificate(block).Build();
    }

    private static string Hash(byte[] image) => Convert.ToHexString(EfiBinary.Parse(image).ComputeAuthenticodeHash(HashAlgorithmName.SHA256));

    [Theory]
    [InlineData("EFI/BOOT/BOOTX64.EFI", EfiFileRole.FallbackLoader)]
    [InlineData("\\EFI\\BOOT\\BOOTX64.EFI", EfiFileRole.FallbackLoader)]
    [InlineData("efi/boot/bootaa64.efi", EfiFileRole.FallbackLoader)]
    [InlineData("efi.img:/EFI/BOOT/BOOTIA32.EFI", EfiFileRole.FallbackLoader)]
    [InlineData("EFI/BOOT/BOOTRISCV64.EFI", EfiFileRole.FallbackLoader)]
    [InlineData("EFI/BOOT/grubx64.efi", EfiFileRole.Other)]
    [InlineData("EFI/BOOT/BOOTX64.EFI.bak", EfiFileRole.Other)]
    [InlineData("EFI/ubuntu/shimx64.efi", EfiFileRole.Other)]
    [InlineData("EFI/Microsoft/Boot/bootmgfw.efi", EfiFileRole.WindowsBootManager)]
    [InlineData("\\EFI\\Microsoft\\Boot\\cdboot.efi", EfiFileRole.WindowsBootManager)]
    [InlineData("EFI/Microsoft/Boot/en-US/readme.txt", EfiFileRole.Other)]
    public void Role_IsDerivedFromThePath(string path, EfiFileRole expected)
    {
        var report = Run((path, PeBuilder.Typical().Build()));

        Assert.Equal(expected, report.Files[0].Role);
    }

    [Fact]
    public void Analyze_NoFiles_ReportsNoEfiFiles()
    {
        var report = new EfiMediaAnalyzer().Analyze([]);

        Assert.Equal(EfiMediaVerdict.NoEfiFiles, report.Verdict);
        Assert.Empty(report.Files);
        Assert.Empty(report.Matrix.Cells);
        Assert.NotEmpty(report.RevocationSources);
    }

    [Fact]
    public void Analyze_UnreadableFile_IsReportedAndTheRunContinues()
    {
        var logger = new ListLogger();
        var analyzer = new EfiMediaAnalyzer(logger: logger);
        var garbage = PeBuilder.Pattern(5000, 3);

        var report = analyzer.Analyze(
        [
            ("EFI/BOOT/BOOTX64.EFI", new MemoryStream(garbage)),
            ("EFI/BOOT/BOOTAA64.EFI", new MemoryStream(PeBuilder.Typical().Build())),
        ]);

        var broken = report.Files[0];
        Assert.False(broken.IsReadable);
        Assert.Null(broken.AuthenticodeSha256);
        Assert.Equal(EfiMessageKeys.Unreadable, Assert.Single(broken.Findings).Key);
        Assert.Equal(5000, broken.Size);
        Assert.True(report.Files[1].IsReadable);
        Assert.Contains(logger.Warnings, w => w.Contains("EFI/BOOT/BOOTX64.EFI", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_OnlyUnreadableFiles_ReportsNoEfiFiles()
    {
        var report = Run(("EFI/BOOT/BOOTX64.EFI", [1, 2, 3]));

        Assert.Equal(EfiMediaVerdict.NoEfiFiles, report.Verdict);
        Assert.Equal(EfiMessageKeys.SummaryNoEfiFiles, Assert.Single(report.Summary).Key);
    }

    [Fact]
    public void Analyze_UnsignedFallbackLoader_NeedsSecureBootSwitchedOff()
    {
        var report = Run(("EFI/BOOT/BOOTX64.EFI", PeBuilder.Typical().Build()));

        var file = Assert.Single(report.Files);
        Assert.Equal(EfiMediaVerdict.NoSignature, report.Verdict);
        Assert.Equal([SignatureAuthority.NoSignature], file.Authorities);
        Assert.Contains(file.Findings, m => m.Key == EfiMessageKeys.NoSignature);
        Assert.Contains(file.Recommendations, m => m.Key == EfiMessageKeys.AdviceSecureBootOff);
        Assert.All(file.Verdicts.Values, v => Assert.Equal(BootVerdict.NoSignature, v));
        Assert.Equal(EfiMachine.X64, file.Machine);
        Assert.Equal(EfiFileRole.FallbackLoader, file.Role);
        Assert.Equal(64, file.AuthenticodeSha256?.Length);
    }

    [Fact]
    public void Analyze_FallbackLoaderSignedByAnotherCa_AsksToEnrollTheKey()
    {
        var report = Run(("EFI/BOOT/BOOTX64.EFI", Signed(PeBuilder.Typical)));

        var file = Assert.Single(report.Files);
        Assert.Equal(EfiMediaVerdict.NotMicrosoftSigned, report.Verdict);
        Assert.Contains(file.Findings, m => m.Key == EfiMessageKeys.OtherSigner && (string?)m.Arguments[1] == "Bootrix Test Signer");
        var advice = Assert.Single(file.Recommendations, m => m.Key == EfiMessageKeys.AdviceEnrollKey);
        Assert.Equal("Bootrix Test Signer", advice.Arguments[1]);
        Assert.All(file.Verdicts.Values, v => Assert.Equal(BootVerdict.SignerNotInDb, v));
    }

    [Fact]
    public void Analyze_LaterStageSignedByAnotherCa_NeedsNoKeyAdvice()
    {
        var report = Run(("EFI/ubuntu/grubx64.efi", Signed(PeBuilder.Typical)));

        Assert.DoesNotContain(report.Files[0].Recommendations, m => m.Key == EfiMessageKeys.AdviceEnrollKey);
    }

    [Fact]
    public void Analyze_ImageChangedAfterSigning_ReportsTheBrokenSignature()
    {
        var image = Signed(PeBuilder.Typical);
        image[0x210] ^= 1;

        var report = Run(("EFI/BOOT/BOOTX64.EFI", image));

        Assert.Equal(EfiMediaVerdict.InvalidSignature, report.Verdict);
        Assert.Contains(report.Files[0].Findings, m => m.Key == EfiMessageKeys.SignatureInvalid);
        Assert.Contains(report.Files[0].Recommendations, m => m.Key == EfiMessageKeys.AdviceFixSignature);
        Assert.All(report.Files[0].Verdicts.Values, v => Assert.Equal(BootVerdict.SignatureInvalid, v));
    }

    [Fact]
    public void Analyze_FallbackNameForAnotherArchitecture_IsPointedOut()
    {
        var image = new PeBuilder { Machine = 0xAA64 }.AddSection(".text", [1, 2, 3]).Build();

        var report = Run(("EFI/BOOT/BOOTX64.EFI", image));

        var finding = Assert.Single(report.Files[0].Findings, m => m.Key == EfiMessageKeys.MachineMismatch);
        Assert.Equal(EfiMachine.Arm64, finding.Arguments[1]);
        Assert.Equal("X64", finding.Arguments[2]);
    }

    [Fact]
    public void Analyze_WindowsExecutableInTheList_IsNotTreatedAsBootLoader()
    {
        var report = Run(("tools/setup.exe", new PeBuilder { Subsystem = 3 }.AddSection(".text", [1]).Build()));

        Assert.Contains(report.Files[0].Findings, m => m.Key == EfiMessageKeys.NotEfiApplication);
        Assert.Equal(EfiMediaVerdict.NoEfiFiles, report.Verdict);
    }

    [Fact]
    public void Analyze_HashListedInTheDbx_IsRevoked()
    {
        // The DBX here is constructed from the hash of the test image; no real revoked loader is involved.
        var image = Signed(PeBuilder.Typical);
        var dbx = RevocationData.FromDbx(DbxBuilder.Sha256List(Hash(image)));

        var report = Run(dbx, ("EFI/BOOT/BOOTX64.EFI", image));

        var file = report.Files[0];
        Assert.True(file.IsRevoked);
        var reason = Assert.Single(file.Revocations);
        Assert.Equal(RevocationKind.DbxHash, reason.Kind);
        Assert.Equal(Hash(image), reason.Message.Arguments[1]);
        Assert.Equal(EfiMessageKeys.AdviceReplaceRevoked, reason.Advice?.Key);
        Assert.Equal(EfiMediaVerdict.Revoked, report.Verdict);
        Assert.True(report.HasRevokedFiles);
        Assert.Equal(BootVerdict.Revoked, file.Verdicts[FirmwareProfileId.Updated2011And2023]);
        Assert.Equal(BootVerdict.SignerNotInDb, file.Verdicts[FirmwareProfileId.Legacy2011]);
        Assert.Contains("EFI/BOOT/BOOTX64.EFI", Assert.Single(report.Summary).Arguments[0]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_UnsignedFileWithAListedHash_IsNotReportedAsRevoked()
    {
        var image = PeBuilder.Typical().Build();
        var dbx = RevocationData.FromDbx(DbxBuilder.Sha256List(Hash(image)));

        var report = Run(dbx, ("EFI/BOOT/BOOTX64.EFI", image));

        Assert.False(report.Files[0].IsRevoked);
    }

    [Fact]
    public void Analyze_SbatGenerationBelowTheLevel_IsRevoked()
    {
        var image = Signed(() => PeBuilder.Typical().AddSection(".sbat", "sbat,1,SBAT Version\ngrub,3,Free Software Foundation\ngrub.vendor,1,Vendor\n"u8.ToArray()));

        var report = Run(("EFI/ubuntu/grubx64.efi", image));

        var reason = Assert.Single(report.Files[0].Revocations);
        Assert.Equal(RevocationKind.SbatGeneration, reason.Kind);
        Assert.Equal("grub", reason.Message.Arguments[1]);
        Assert.Equal(3, reason.Message.Arguments[2]);
        Assert.Equal(EfiMessageKeys.AdviceUpdateSbat, reason.Advice?.Key);
        Assert.Contains("grub,3 < grub,", reason.Message.Format(English), StringComparison.Ordinal);
        Assert.Equal(EfiMediaVerdict.Revoked, report.Verdict);
    }

    [Fact]
    public void Analyze_SbatAtOrAboveTheLevel_IsClean()
    {
        var level = RevocationData.Embedded.SbatLevel!;
        var text = $"sbat,1,SBAT Version\ngrub,{level.MinimumGenerations["grub"]},Free Software Foundation\n";
        var image = Signed(() => PeBuilder.Typical().AddSection(".sbat", System.Text.Encoding.UTF8.GetBytes(text)));

        var report = Run(("EFI/ubuntu/grubx64.efi", image));

        Assert.Empty(report.Files[0].Revocations);
        Assert.Equal(["sbat", "grub"], report.Files[0].Sbat.Select(e => e.Component));
    }

    [Fact]
    public void Analyze_BootManagerSvnBelowTheDbxMinimum_IsRevoked()
    {
        var image = Signed(() => PeBuilder.Typical().AddBootmgrSecurityVersion(7, 0));

        var report = Run(("EFI/Microsoft/Boot/bootmgfw.efi", image));

        var file = report.Files[0];
        var reason = Assert.Single(file.Revocations);
        Assert.Equal(RevocationKind.SecurityVersion, reason.Kind);
        Assert.Equal(new SecurityVersion(7, 0), file.BootmgrSecurityVersion);
        Assert.Equal(EfiMessageKeys.AdviceUpdateBootManager, reason.Advice?.Key);
        Assert.Equal(EfiMediaVerdict.Revoked, report.Verdict);
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(11, 2)]
    public void Analyze_BootManagerSvnAtOrAboveTheMinimum_IsClean(int major, int minor)
    {
        var image = Signed(() => PeBuilder.Typical().AddBootmgrSecurityVersion((ushort)major, (ushort)minor));

        var report = Run(("EFI/Microsoft/Boot/bootmgfw.efi", image));

        Assert.Empty(report.Files[0].Revocations);
    }

    [Fact]
    public void Analyze_SvnRequirementFromADeviceDbx_IsUsed()
    {
        var dbx = DbxBuilder.List(DbxBuilder.Sha256Type, [(DbxBuilder.SvnOwner, DbxBuilder.SvnData(DbxBuilder.BootmgrSvn, 12, 0))]);
        var image = Signed(() => PeBuilder.Typical().AddBootmgrSecurityVersion(11, 0));

        var report = Run(RevocationData.FromDbx(dbx), ("EFI/Microsoft/Boot/bootmgfw.efi", image));

        Assert.Equal(RevocationKind.SecurityVersion, Assert.Single(report.Files[0].Revocations).Kind);
    }

    [Fact]
    public void Analyze_RevokedCaInTheChain_IsReportedButDoesNotMarkTheFileRevoked()
    {
        var dbx = RevocationData.FromDbx(DbxBuilder.List(DbxBuilder.X509Type, [(DbxBuilder.SomeOwner, authorities.Own.Ca.RawData)]));
        var image = Signed(PeBuilder.Typical);

        var report = Run(dbx, ("EFI/BOOT/BOOTX64.EFI", image));

        var file = report.Files[0];
        Assert.Equal(RevocationKind.Certificate, Assert.Single(file.Revocations).Kind);
        Assert.False(file.IsRevoked);
        Assert.Equal("Bootrix Test CA", file.Signatures[0].RevokedCertificate);
        Assert.NotEqual(EfiMediaVerdict.Revoked, report.Verdict);
    }

    [Fact]
    public void Analyze_RevokedLaterStage_RevokesTheMediumEvenWhenTheEntryPointIsFine()
    {
        var entry = Signed(PeBuilder.Typical);
        var grub = Signed(() => PeBuilder.Typical().AddSection(".sbat", "grub,1,x\n"u8.ToArray()));

        var report = Run(("EFI/BOOT/BOOTX64.EFI", entry), ("EFI/BOOT/grubx64.efi", grub));

        Assert.Equal(EfiMediaVerdict.Revoked, report.Verdict);
        Assert.Equal("EFI/BOOT/grubx64.efi", Assert.Single(report.Summary).Arguments[0]);
        Assert.False(report.Files[0].IsRevoked);
    }

    [Fact]
    public void Analyze_ReadsTheStreamFromItsCurrentPosition()
    {
        var image = PeBuilder.Typical().Build();
        var stream = new MemoryStream([.. new byte[5], .. image]) { Position = 5 };

        var report = new EfiMediaAnalyzer().Analyze([("EFI/BOOT/BOOTX64.EFI", stream)]);

        Assert.True(report.Files[0].IsReadable);
        Assert.Equal(image.Length, report.Files[0].Size);
    }

    [Fact]
    public void Analyze_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new EfiMediaAnalyzer().Analyze([("EFI/BOOT/BOOTX64.EFI", new MemoryStream(PeBuilder.Typical().Build()))], cts.Token));
    }

    [Fact]
    public void Analyze_ReportsWhereTheRevocationDataCameFrom()
    {
        var report = Run(("EFI/BOOT/BOOTX64.EFI", PeBuilder.Typical().Build()));

        Assert.Equal(RevocationData.Embedded.Retrieved, report.RevocationDataDate);
        Assert.Equal(["microsoft/secureboot_objects", "rhboot/shim"], report.RevocationSources.Select(s => s.Repository));
    }

    [Fact]
    public void Analyze_DamagedSignedImages_NeverThrow()
    {
        var original = PeBuilder.Typical().AddBootmgrSecurityVersion(7, 0).AddSection(".sbat", "grub,1,x\n"u8.ToArray()).AddCertificate(Fixtures.Read("ms-uefi-ca-2011.p7")).Build();
        var analyzer = new EfiMediaAnalyzer();
        var random = new Random(99);

        for (var round = 0; round < 600; round++)
        {
            var copy = (byte[])original.Clone();
            for (var change = 0; change < 1 + random.Next(5); change++)
            {
                copy[random.Next(3) == 0 ? random.Next(copy.Length) : copy.Length - 1 - random.Next(9800)] = (byte)random.Next(256);
            }

            var report = analyzer.Analyze([("EFI/BOOT/BOOTX64.EFI", new MemoryStream(copy))]);

            Assert.Single(report.Files);
        }
    }

    [Fact]
    public void Summarize_PrefersTheFallbackLoadersOverTheOtherFiles()
    {
        var fallback = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(SignatureAuthority.MicrosoftUefiCa2011), Sig(SignatureAuthority.MicrosoftUefiCa2023)]);
        var grub = Report("EFI/ubuntu/grubx64.efi", role: EfiFileRole.Other, signatures: [Sig(SignatureAuthority.Other)]);

        var report = new EfiMediaAnalyzer().Summarize([fallback, grub]);

        Assert.Equal(EfiMediaVerdict.Both2011And2023, report.Verdict);
        Assert.Equal(["EFI/BOOT/BOOTX64.EFI"], report.Matrix.Cells.SelectMany(c => c.Files).Distinct());
    }

    [Fact]
    public void Summarize_WithoutFallbackLoader_UsesTheWindowsBootManager()
    {
        var manager = Report("EFI/Microsoft/Boot/bootmgfw.efi", role: EfiFileRole.WindowsBootManager, signatures: [Sig(SignatureAuthority.WindowsUefiCa2023)]);
        var other = Report("EFI/x/other.efi", role: EfiFileRole.Other, signatures: [Sig(SignatureAuthority.Other)]);

        var report = new EfiMediaAnalyzer().Summarize([other, manager]);

        Assert.Equal(EfiMediaVerdict.Only2023, report.Verdict);
    }

    [Fact]
    public void Summarize_WithoutRolesAtAll_UsesEveryEfiImage()
    {
        var report = new EfiMediaAnalyzer().Summarize([Report("x/a.efi", role: EfiFileRole.Other, signatures: [Sig(SignatureAuthority.MicrosoftUefiCa2011)])]);

        Assert.Equal(EfiMediaVerdict.Only2011, report.Verdict);
    }

    [Theory]
    [InlineData(new[] { SignatureAuthority.WindowsProductionPca2011 }, EfiMediaVerdict.Only2011)]
    [InlineData(new[] { SignatureAuthority.MicrosoftUefiCa2011 }, EfiMediaVerdict.Only2011)]
    [InlineData(new[] { SignatureAuthority.WindowsUefiCa2023 }, EfiMediaVerdict.Only2023)]
    [InlineData(new[] { SignatureAuthority.MicrosoftUefiCa2023 }, EfiMediaVerdict.Only2023)]
    [InlineData(new[] { SignatureAuthority.MicrosoftUefiCa2011, SignatureAuthority.MicrosoftUefiCa2023 }, EfiMediaVerdict.Both2011And2023)]
    [InlineData(new[] { SignatureAuthority.WindowsProductionPca2011, SignatureAuthority.WindowsUefiCa2023 }, EfiMediaVerdict.Both2011And2023)]
    [InlineData(new[] { SignatureAuthority.Other }, EfiMediaVerdict.NotMicrosoftSigned)]
    public void Summarize_MapsTheSignaturesToAMediaVerdict(SignatureAuthority[] authoritiesOfTheEntry, EfiMediaVerdict expected)
    {
        var file = Report("EFI/BOOT/BOOTX64.EFI", signatures: authoritiesOfTheEntry.Select(a => Sig(a)));

        Assert.Equal(expected, new EfiMediaAnalyzer().Summarize([file]).Verdict);
    }

    [Fact]
    public void Summarize_ThirdPartyEntryPoints_RequireTheThirdPartyCa()
    {
        var shim = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(SignatureAuthority.MicrosoftUefiCa2011), Sig(SignatureAuthority.MicrosoftUefiCa2023)]);
        var windows = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(SignatureAuthority.WindowsUefiCa2023)]);

        var analyzer = new EfiMediaAnalyzer();
        var thirdParty = analyzer.Summarize([shim]);
        var microsoftOnly = analyzer.Summarize([windows]);

        Assert.True(thirdParty.RequiresThirdPartyCa);
        Assert.Contains(thirdParty.Recommendations, m => m.Key == EfiMessageKeys.AdviceThirdPartyCa);
        Assert.False(microsoftOnly.RequiresThirdPartyCa);
        Assert.DoesNotContain(microsoftOnly.Recommendations, m => m.Key == EfiMessageKeys.AdviceThirdPartyCa);
    }

    [Fact]
    public void Summarize_RecommendationsOfSeveralFilesAreDeduplicated()
    {
        var advice = new EfiMessage(EfiMessageKeys.AdviceWindows2023, "EFI/BOOT/BOOTX64.EFI");
        var first = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(SignatureAuthority.WindowsProductionPca2011)]) with { Recommendations = [advice] };
        var second = first with { Path = "EFI/BOOT/BOOTX64.EFI", Recommendations = [new EfiMessage(EfiMessageKeys.AdviceWindows2023, "EFI/BOOT/BOOTX64.EFI")] };

        var report = new EfiMediaAnalyzer().Summarize([first, second]);

        Assert.Single(report.Recommendations);
    }

    [Fact]
    public void EfiMessage_ComparesArgumentsByValue()
    {
        Assert.Equal(new EfiMessage("k", "a", 1), new EfiMessage("k", "a", 1));
        Assert.NotEqual(new EfiMessage("k", "a", 1), new EfiMessage("k", "a", 2));
        Assert.Equal(new EfiMessage("k", "a").GetHashCode(), new EfiMessage("k", "a").GetHashCode());
    }

    [Fact]
    public void Messages_EveryKeyHasGermanAndEnglishText()
    {
        var keys = typeof(EfiMessageKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Concat(Enum.GetValues<FirmwareProfileId>().Select(EfiMessageKeys.ProfileName))
            .Concat(Enum.GetValues<BootVerdict>().Select(EfiMessageKeys.VerdictName))
            .ToList();

        Assert.True(keys.Count > 30);
        foreach (var key in keys)
        {
            foreach (var localizer in new[] { German, English })
            {
                Assert.True(localizer.Has(key), $"{key} is missing for {localizer.Culture.Name}");
                Assert.NotEqual(key, localizer.Get(key));
                _ = localizer.Get(key, "a", "b", "c", "d", "e");
            }

            Assert.NotEqual(German.Get(key), English.Get(key));
        }
    }

    [Fact]
    public void Messages_GermanAndEnglishTextsUseTheSamePlaceholders()
    {
        var keys = typeof(EfiMessageKeys).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

        foreach (var key in keys)
        {
            Assert.Equal(Placeholders(German.Get(key)), Placeholders(English.Get(key)));
        }
    }

    private static string Placeholders(string text) =>
        string.Join(',', System.Text.RegularExpressions.Regex.Matches(text, @"\{\d\}").Select(m => m.Value).Distinct().Order());

    [Fact]
    public void Messages_AreFormattedInBothLanguages()
    {
        var image = Signed(() => PeBuilder.Typical().AddSection(".sbat", "grub,1,x\n"u8.ToArray()).AddBootmgrSecurityVersion(7, 0));
        var report = Run(("EFI/BOOT/BOOTX64.EFI", image), ("EFI/BOOT/BOOTAA64.EFI", PeBuilder.Typical().Build()), ("EFI/x/bad.efi", [1, 2, 3]));

        var messages = report.Files
            .SelectMany(f => f.Findings.Concat(f.Recommendations).Concat(f.Revocations.Select(r => r.Message)))
            .Concat(report.Summary)
            .Concat(report.Recommendations)
            .ToList();

        Assert.True(messages.Count >= 6);
        foreach (var message in messages)
        {
            Assert.DoesNotContain("{", message.Format(German), StringComparison.Ordinal);
            Assert.DoesNotContain("{", message.Format(English), StringComparison.Ordinal);
        }
    }

    [RealSampleFact]
    public void RealRevokedUbuntuShims_AreFlaggedAndNewerOnesAreNot()
    {
        var revoked = new[] { "shim-ubuntu-0.4-x64.efi", "shim-ubuntu-15.0-x64.efi" };
        var current = new[] { "shim-ubuntu-15.4-x64.efi", "shim-ubuntu-15.8-x64.efi", "shim-debian-16.1-x64.efi", "shim-almalinux-16.1-x64.efi" };

        foreach (var file in revoked.Concat(current))
        {
            var report = Analyze(("EFI/BOOT/BOOTX64.EFI", file));

            var listedInDbx = report.Files[0].Revocations.Any(r => r.Kind == RevocationKind.DbxHash);

            Assert.Equal(revoked.Contains(file), listedInDbx);
            if (listedInDbx)
            {
                Assert.Equal(EfiMediaVerdict.Revoked, report.Verdict);
            }
        }
    }

    [RealSampleFact]
    public void RealDebianShim_DualSignedWithBothThirdPartyCas_BootsOnEverythingButSecuredCore()
    {
        var report = Analyze(("EFI/BOOT/BOOTX64.EFI", "shim-debian-16.1-x64.efi"));

        var file = report.Files[0];
        Assert.Equal([SignatureAuthority.MicrosoftUefiCa2011, SignatureAuthority.MicrosoftUefiCa2023], file.Authorities);
        Assert.All(file.Signatures, s => Assert.True(s.IsIntact));
        Assert.Empty(file.Revocations);
        Assert.Equal(EfiMediaVerdict.Both2011And2023, report.Verdict);
        Assert.True(report.RequiresThirdPartyCa);
        Assert.Equal(
            [BootVerdict.Boots, BootVerdict.Boots, BootVerdict.Boots, BootVerdict.SignerNotInDb, BootVerdict.Boots],
            FirmwareProfile.All.Select(p => report.Matrix.Get(p.Id, EfiMachine.X64)!.Value));
        Assert.Contains(file.Sbat, e => e is { Component: "shim.debian" });
    }

    [RealSampleFact]
    public void RealAlmaLinuxShim_X64DualSignedAndArm64SignedOnlyWith2023()
    {
        var x64 = Analyze(("EFI/BOOT/BOOTX64.EFI", "shim-almalinux-16.1-x64.efi"));
        var arm64 = Analyze(("EFI/BOOT/BOOTAA64.EFI", "shim-almalinux-16.1-aa64.efi"));

        Assert.Equal(EfiMediaVerdict.Both2011And2023, x64.Verdict);
        Assert.Equal(EfiMediaVerdict.Only2023, arm64.Verdict);
        Assert.Equal(EfiMachine.Arm64, arm64.Files[0].Machine);
        Assert.Equal(BootVerdict.SignerNotInDb, arm64.Matrix.Get(FirmwareProfileId.Legacy2011, EfiMachine.Arm64));
        Assert.Equal(BootVerdict.Boots, arm64.Matrix.Get(FirmwareProfileId.Only2023, EfiMachine.Arm64));
    }

    [RealSampleFact]
    public void RealUbuntuShim15_8_SignedOnlyWith2011_AdvisesADualSignedShim()
    {
        var report = Analyze(("EFI/BOOT/BOOTX64.EFI", "shim-ubuntu-15.8-x64.efi"));

        Assert.Equal(EfiMediaVerdict.Only2011, report.Verdict);
        Assert.Contains(report.Recommendations, m => m.Key == EfiMessageKeys.AdviceDualSignedLoader);
        Assert.Equal(BootVerdict.SignerNotInDb, report.Matrix.Get(FirmwareProfileId.Only2023, EfiMachine.X64));
    }

    [RealSampleFact]
    public void RealUbuntuGrub_BelowTheNovember2025SbatLevel_RevokesTheMedium()
    {
        var report = Analyze(("EFI/BOOT/BOOTX64.EFI", "shim-debian-16.1-x64.efi"), ("EFI/ubuntu/grubx64.efi", "grub-ubuntu-2.12-x64.efi"));

        var grub = report.Files[1];
        var reason = Assert.Single(grub.Revocations);
        Assert.Equal(RevocationKind.SbatGeneration, reason.Kind);
        Assert.Equal(("grub", 5), (reason.Message.Arguments[1], reason.Message.Arguments[2]));
        Assert.Equal(SignatureAuthority.Other, Assert.Single(grub.Authorities));
        Assert.Equal(EfiMediaVerdict.Revoked, report.Verdict);
        Assert.False(report.Files[0].IsRevoked);
    }

    [RealSampleFact]
    public void RealWindowsBootManager_SignedWithPca2011_FailsOnceTheCaIsRevoked()
    {
        var report = Analyze(("EFI/Microsoft/Boot/bootmgfw.efi", "bootmgfw-windows-old-x64.efi"));

        var file = report.Files[0];
        Assert.Equal(EfiFileRole.WindowsBootManager, file.Role);
        Assert.Equal([SignatureAuthority.WindowsProductionPca2011], file.Authorities);
        Assert.Contains(file.Revocations, r => r.Kind == RevocationKind.Certificate);
        Assert.False(file.IsRevoked);
        Assert.Null(file.BootmgrSecurityVersion); // this old build has no BOOTMGRSECURITYVERSIONNUMBER resource
        Assert.Equal(EfiMediaVerdict.Only2011, report.Verdict);
        Assert.Equal(BootVerdict.Revoked, report.Matrix.Get(FirmwareProfileId.Pca2011Revoked, EfiMachine.X64));
        Assert.Contains(report.Recommendations, m => m.Key == EfiMessageKeys.AdviceWindows2023);
    }

    [RealSampleFact]
    public void RealMultiArchitectureMedium_ShowsOneColumnPerArchitecture()
    {
        var report = Analyze(("EFI/BOOT/BOOTX64.EFI", "shim-debian-16.1-x64.efi"), ("EFI/BOOT/BOOTAA64.EFI", "shim-almalinux-16.1-aa64.efi"));

        Assert.Equal(10, report.Matrix.Cells.Count);
        Assert.Equal(BootVerdict.Boots, report.Matrix.Get(FirmwareProfileId.Legacy2011, EfiMachine.X64));
        Assert.Equal(BootVerdict.SignerNotInDb, report.Matrix.Get(FirmwareProfileId.Legacy2011, EfiMachine.Arm64));
        Assert.Equal(EfiMediaVerdict.Only2023, report.Verdict);
    }

    [RealSampleFact]
    public void RealFiles_AnalysedTogether_EachKeepsItsOwnResult()
    {
        var files = new[]
        {
            "shim-debian-16.1-x64.efi", "shim-ubuntu-15.8-x64.efi", "fallback-ubuntu-15.8-x64.efi", "mokmanager-ubuntu-15.8-x64.efi",
            "grub-almalinux-2.12-x64.efi", "bootmgfw-windows-old-x64.efi",
        };

        var report = Analyze(files.Select(f => ("EFI/x/" + f, f)).ToArray());

        Assert.Equal(files.Length, report.Files.Count);
        Assert.All(report.Files, f => Assert.True(f.IsReadable));
        Assert.All(report.Files, f => Assert.All(f.Signatures, s => Assert.True(s.IsIntact, f.Path)));
    }

    private static EfiAnalysisReport Analyze(params (string Path, string Sample)[] files)
    {
        foreach (var (_, sample) in files)
        {
            Assert.True(RealSamples.Exists(sample), sample + " is missing from BOOTRIX_EFI_SAMPLES");
        }

        var streams = files.Select(f => (f.Path, (Stream)File.OpenRead(RealSamples.Path(f.Sample)))).ToList();
        try
        {
            return new EfiMediaAnalyzer().Analyze(streams);
        }
        finally
        {
            foreach (var (_, stream) in streams)
            {
                stream.Dispose();
            }
        }
    }

    private sealed class ListLogger : ILogger<EfiMediaAnalyzer>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
