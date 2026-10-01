// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection.PortableExecutable;
using Bootrix.Core.Boot;
using static Bootrix.Core.Boot.BootVerdict;
using static Bootrix.Core.Boot.FirmwareProfileId;
using static Bootrix.Core.Boot.SignatureAuthority;

namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// The matrix and the media verdict are tested with hand-built file reports: a Microsoft-signed file that matches its
/// content can only be produced by Microsoft, so these tests state the signature facts directly.
/// </summary>
public class CompatibilityMatrixTests
{
    internal static EfiSignature Sig(SignatureAuthority authority, bool intact = true, string? revokedCertificate = null) =>
        new(authority, null, [], "SHA256", intact, intact, false, null, revokedCertificate);

    internal static EfiFileReport Report(
        string path,
        EfiMachine machine = EfiMachine.X64,
        EfiFileRole role = EfiFileRole.FallbackLoader,
        IEnumerable<EfiSignature>? signatures = null,
        IEnumerable<RevocationReason>? revocations = null,
        Subsystem subsystem = Subsystem.EfiApplication)
    {
        var report = new EfiFileReport(
            path,
            role,
            1000,
            machine,
            subsystem,
            new string('A', 64),
            [.. signatures ?? []],
            [],
            null,
            [.. revocations ?? []],
            [],
            [],
            new Dictionary<FirmwareProfileId, BootVerdict>());
        return report with { Verdicts = FirmwareProfile.All.ToDictionary(p => p.Id, p => p.Evaluate(report)) };
    }

    private static readonly RevocationReason HashRevoked = new(RevocationKind.DbxHash, new EfiMessage("x"));

    public static TheoryData<string, SignatureAuthority[], BootVerdict[]> Scenarios => new()
    {
        // Verdicts in the order Legacy2011, Updated2011And2023, Only2023, SecuredCore, Pca2011Revoked.
        { "Windows boot manager, 2011 only", [WindowsProductionPca2011], [Boots, Boots, SignerNotInDb, SignerNotInDb, BootVerdict.Revoked] },
        { "Windows boot manager, 2023", [WindowsUefiCa2023], [SignerNotInDb, Boots, Boots, Boots, Boots] },
        { "Windows boot manager, 2011 and 2023", [WindowsProductionPca2011, WindowsUefiCa2023], [Boots, Boots, Boots, Boots, Boots] },
        { "shim, 2011 only", [MicrosoftUefiCa2011], [Boots, Boots, SignerNotInDb, SignerNotInDb, Boots] },
        { "shim, 2011 and 2023", [MicrosoftUefiCa2011, MicrosoftUefiCa2023], [Boots, Boots, Boots, SignerNotInDb, Boots] },
        { "shim, 2023 only", [MicrosoftUefiCa2023], [SignerNotInDb, Boots, Boots, SignerNotInDb, Boots] },
        { "option ROM CA 2023", [MicrosoftOptionRomUefiCa2023], [SignerNotInDb, Boots, Boots, SignerNotInDb, Boots] },
        { "other CA", [Other], [SignerNotInDb, SignerNotInDb, SignerNotInDb, SignerNotInDb, SignerNotInDb] },
        { "other CA next to the Windows 2023 CA", [Other, WindowsUefiCa2023], [SignerNotInDb, Boots, Boots, Boots, Boots] },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Matrix_ShowsWhichFirmwareProfilesAcceptTheSignature(string name, SignatureAuthority[] authorities, BootVerdict[] expected)
    {
        _ = name;
        var file = Report("EFI/BOOT/BOOTX64.EFI", signatures: authorities.Select(a => Sig(a)));

        var matrix = CompatibilityMatrix.Build([file]);

        Assert.Equal(expected, FirmwareProfile.All.Select(p => matrix.Get(p.Id, EfiMachine.X64)!.Value));
    }

    [Fact]
    public void Matrix_UnsignedFile_BootsNowhereWithSecureBoot()
    {
        var matrix = CompatibilityMatrix.Build([Report("EFI/BOOT/BOOTX64.EFI")]);

        Assert.All(matrix.Cells, c => Assert.Equal(BootVerdict.NoSignature, c.Verdict));
    }

    [Fact]
    public void Matrix_SignatureThatDoesNotMatchTheFile_IsInvalidEverywhere()
    {
        var matrix = CompatibilityMatrix.Build([Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(MicrosoftUefiCa2011, intact: false)])]);

        Assert.All(matrix.Cells, c => Assert.Equal(SignatureInvalid, c.Verdict));
    }

    [Fact]
    public void Matrix_RevokedHash_BlocksEveryProfileThatAppliesTheCurrentDbx()
    {
        var file = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(MicrosoftUefiCa2011)], revocations: [HashRevoked]);

        var matrix = CompatibilityMatrix.Build([file]);

        Assert.Equal(Boots, matrix.Get(Legacy2011, EfiMachine.X64));
        Assert.Equal(BootVerdict.Revoked, matrix.Get(Updated2011And2023, EfiMachine.X64));
        Assert.Equal(BootVerdict.Revoked, matrix.Get(Only2023, EfiMachine.X64));
        Assert.Equal(BootVerdict.Revoked, matrix.Get(Pca2011Revoked, EfiMachine.X64));
        Assert.True(matrix.AnyRevoked(Updated2011And2023));
        Assert.False(matrix.AnyRevoked(Legacy2011));
    }

    [Fact]
    public void Matrix_RevokedCertificateAlone_DoesNotCountAsRevokedFile()
    {
        var reason = new RevocationReason(RevocationKind.Certificate, new EfiMessage("x"));
        var file = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(WindowsProductionPca2011, revokedCertificate: "Microsoft Windows Production PCA 2011")], revocations: [reason]);

        Assert.False(file.IsRevoked);
        Assert.Equal(Boots, CompatibilityMatrix.Build([file]).Get(Updated2011And2023, EfiMachine.X64));
    }

    [Fact]
    public void Matrix_ShowsTheWorstEntryPerArchitectureAndNamesTheCulprit()
    {
        var good = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(MicrosoftUefiCa2023)]);
        var bad = Report("efi.img/EFI/BOOT/BOOTX64.EFI", signatures: [Sig(MicrosoftUefiCa2011)]);

        var cell = CompatibilityMatrix.Build([good, bad]).Cells.Single(c => c.Profile == Only2023);

        Assert.Equal(SignerNotInDb, cell.Verdict);
        Assert.Equal(["efi.img/EFI/BOOT/BOOTX64.EFI"], cell.Files);
    }

    [Fact]
    public void Matrix_HasOneColumnPerArchitecture()
    {
        var x64 = Report("EFI/BOOT/BOOTX64.EFI", signatures: [Sig(MicrosoftUefiCa2011), Sig(MicrosoftUefiCa2023)]);
        var arm64 = Report("EFI/BOOT/BOOTAA64.EFI", EfiMachine.Arm64, signatures: [Sig(MicrosoftUefiCa2023)]);

        var matrix = CompatibilityMatrix.Build([arm64, x64]);

        Assert.Equal(10, matrix.Cells.Count);
        Assert.Equal(Boots, matrix.Get(Legacy2011, EfiMachine.X64));
        Assert.Equal(SignerNotInDb, matrix.Get(Legacy2011, EfiMachine.Arm64));
        Assert.Null(matrix.Get(Legacy2011, EfiMachine.X86));
        Assert.False(matrix.Boots(Legacy2011));
        Assert.True(matrix.Boots(Only2023));
    }

    [Fact]
    public void Matrix_WithoutFiles_BootsNowhere()
    {
        var matrix = CompatibilityMatrix.Build([]);

        Assert.Empty(matrix.Cells);
        Assert.False(matrix.Boots(Updated2011And2023));
        Assert.False(matrix.AnyRevoked(Updated2011And2023));
    }

    [Fact]
    public void Matrix_IgnoresUnreadableFiles()
    {
        var report = new EfiMediaAnalyzer().Analyze([("EFI/BOOT/BOOTX64.EFI", new MemoryStream([1, 2, 3]))]);

        Assert.False(report.Files[0].IsReadable);
        Assert.Empty(CompatibilityMatrix.Build(report.Files).Cells);
    }

    [Fact]
    public void Profiles_DescribeTheDbContentsPerFirmwareClass()
    {
        var byId = FirmwareProfile.All.ToDictionary(p => p.Id);

        Assert.Equal(5, byId.Count);
        Assert.Equal([WindowsProductionPca2011, MicrosoftUefiCa2011], byId[Legacy2011].TrustedAuthorities.Order());
        Assert.False(byId[Legacy2011].AppliesCurrentRevocations);
        Assert.Equal([WindowsUefiCa2023], byId[SecuredCore].TrustedAuthorities);
        Assert.DoesNotContain(MicrosoftUefiCa2011, byId[Only2023].TrustedAuthorities);
        Assert.Contains(MicrosoftUefiCa2023, byId[Only2023].TrustedAuthorities);
        Assert.Equal([WindowsProductionPca2011], byId[Pca2011Revoked].RevokedAuthorities);
        Assert.Contains(WindowsProductionPca2011, byId[Pca2011Revoked].TrustedAuthorities);
    }

    [Fact]
    public void Authorities_AreGroupedByYearAndRole()
    {
        Assert.True(SignatureAuthorityInfo.Is2011(WindowsProductionPca2011));
        Assert.True(SignatureAuthorityInfo.Is2011(MicrosoftUefiCa2011));
        Assert.True(SignatureAuthorityInfo.Is2023(WindowsUefiCa2023));
        Assert.True(SignatureAuthorityInfo.Is2023(MicrosoftOptionRomUefiCa2023));
        Assert.True(SignatureAuthorityInfo.IsThirdParty(MicrosoftUefiCa2023));
        Assert.False(SignatureAuthorityInfo.IsThirdParty(WindowsUefiCa2023));
        Assert.False(SignatureAuthorityInfo.IsThirdParty(MicrosoftOptionRomUefiCa2023));
        Assert.All(Enum.GetValues<SignatureAuthority>(), a => Assert.False(string.IsNullOrEmpty(SignatureAuthorityInfo.DisplayName(a))));
        Assert.Equal("Windows UEFI CA 2023", SignatureAuthorityInfo.DisplayName(WindowsUefiCa2023));
    }
}
