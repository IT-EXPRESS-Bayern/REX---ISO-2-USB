// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class Windows11RequirementTests
{
    private const long GiB = 1L << 30;

    private static RequirementCheck Check(TargetPcInfo info, Windows11Requirement requirement) =>
        Windows11Requirements.Evaluate(info).Single(c => c.Requirement == requirement);

    private static TargetPcInfo WithMemory(long? installed, long? usable) =>
        new() { Memory = new MemoryInfo { InstalledBytes = installed, UsableBytes = usable } };

    private static TargetPcInfo WithDisks(params long[] sizes) =>
        new() { Disks = [.. sizes.Select((size, i) => TargetPcProfiles.Disk(i, $"Disk {i}", size, BusType.Nvme, DiskMediaType.Ssd))] };

    [Theory]
    [InlineData(4, CheckStatus.Pass)]
    [InlineData(8, CheckStatus.Pass)]
    [InlineData(3, CheckStatus.Fail)]
    [InlineData(2, CheckStatus.Fail)]
    public void Ram_InstalledMemoryIsComparedWithFourGiB(long gib, CheckStatus expected)
    {
        var check = Check(WithMemory(gib * GiB, (long)(gib * GiB * 0.97)), Windows11Requirement.Ram);

        Assert.Equal(expected, check.Status);
        Assert.Equal(expected == CheckStatus.Fail ? LabConfigBypass.Ram : null, check.Bypass);
    }

    [Fact]
    public void Ram_InstalledFourGiBPassesDespiteTheFirmwareReservation()
    {
        Assert.Equal(CheckStatus.Pass, Check(WithMemory(4 * GiB, (long)(3.6 * GiB)), Windows11Requirement.Ram).Status);
    }

    [Theory]
    [InlineData(3.9, CheckStatus.Unknown)]
    [InlineData(3.0, CheckStatus.Unknown)]
    [InlineData(2.9, CheckStatus.Fail)]
    [InlineData(4.0, CheckStatus.Pass)]
    [InlineData(15.8, CheckStatus.Pass)]
    public void Ram_OnlyUsableMemory_LeavesTheBorderUndecided(double usableGib, CheckStatus expected)
    {
        Assert.Equal(expected, Check(WithMemory(null, (long)(usableGib * GiB)), Windows11Requirement.Ram).Status);
    }

    [Fact]
    public void Ram_NoData_IsUnknown()
    {
        Assert.Equal(CheckStatus.Unknown, Check(new TargetPcInfo(), Windows11Requirement.Ram).Status);
        Assert.Equal(CheckStatus.Unknown, Check(WithMemory(null, null), Windows11Requirement.Ram).Status);
    }

    [Theory]
    [InlineData(64, CheckStatus.Pass)]
    [InlineData(512, CheckStatus.Pass)]
    [InlineData(58, CheckStatus.Unknown)]
    [InlineData(63, CheckStatus.Unknown)]
    [InlineData(57, CheckStatus.Fail)]
    [InlineData(32, CheckStatus.Fail)]
    public void Storage_LargestInternalDiskDecides(long gib, CheckStatus expected)
    {
        Assert.Equal(expected, Check(WithDisks(gib * GiB), Windows11Requirement.Storage).Status);
    }

    [Fact]
    public void Storage_ASmallSystemDiskNextToALargeDataDisk_Passes()
    {
        Assert.Equal(CheckStatus.Pass, Check(WithDisks(32 * GiB, 2000 * GiB), Windows11Requirement.Storage).Status);
    }

    [Fact]
    public void Storage_UsbSticksAndCardsDoNotCount()
    {
        var info = new TargetPcInfo
        {
            Disks =
            [
                TargetPcProfiles.Disk(0, "Stick", 256 * GiB, BusType.Usb, DiskMediaType.Unknown),
                TargetPcProfiles.Disk(1, "Mounted image", 256 * GiB, BusType.FileBackedVirtual, DiskMediaType.Unknown),
                TargetPcProfiles.Disk(2, "Card", 256 * GiB, BusType.Sd, DiskMediaType.Unknown) with { IsRemovable = true },
                TargetPcProfiles.Disk(3, "eMMC", 32 * GiB, BusType.Mmc, DiskMediaType.Ssd),
            ],
        };

        var check = Check(info, Windows11Requirement.Storage);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(LabConfigBypass.Storage, check.Bypass);
    }

    [Fact]
    public void Storage_NoInternalDisk_IsUnknown()
    {
        Assert.Equal(CheckStatus.Unknown, Check(new TargetPcInfo { Disks = [] }, Windows11Requirement.Storage).Status);
        Assert.Equal(CheckStatus.Unknown, Check(new TargetPcInfo(), Windows11Requirement.Storage).Status);
    }

    [Theory]
    [InlineData(1, 1, CheckStatus.Fail)]
    [InlineData(2, 2, CheckStatus.Pass)]
    [InlineData(2, 4, CheckStatus.Pass)]
    [InlineData(1, 2, CheckStatus.Fail)]
    public void Cores_PhysicalCoresAreComparedWithTwo(int physical, int logical, CheckStatus expected)
    {
        var info = new TargetPcInfo { Cpu = new CpuInfo { PhysicalCores = physical, LogicalProcessors = logical } };

        Assert.Equal(expected, Check(info, Windows11Requirement.Cores).Status);
    }

    [Theory]
    [InlineData(1, CheckStatus.Fail)]
    [InlineData(2, CheckStatus.Unknown)]
    [InlineData(3, CheckStatus.Unknown)]
    [InlineData(4, CheckStatus.Pass)]
    [InlineData(16, CheckStatus.Pass)]
    public void Cores_WithoutThePhysicalCount_LogicalProcessorsDecideWhatTheyCan(int logical, CheckStatus expected)
    {
        var info = new TargetPcInfo { Cpu = new CpuInfo { LogicalProcessors = logical } };

        Assert.Equal(expected, Check(info, Windows11Requirement.Cores).Status);
    }

    [Theory]
    [InlineData(999, CheckStatus.Fail)]
    [InlineData(1000, CheckStatus.Pass)]
    [InlineData(3600, CheckStatus.Pass)]
    public void Clock_OneGigahertzIsTheLimit(int mhz, CheckStatus expected)
    {
        Assert.Equal(expected, Check(new TargetPcInfo { Cpu = new CpuInfo { MaxClockMhz = mhz } }, Windows11Requirement.Clock).Status);
    }

    [Fact]
    public void Clock_Unknown_IsNotChecked()
    {
        Assert.Equal(CheckStatus.NotChecked, Check(new TargetPcInfo { Cpu = new CpuInfo() }, Windows11Requirement.Clock).Status);
    }

    [Theory]
    [InlineData(CpuArchitecture.X64, null, CheckStatus.Pass)]
    [InlineData(CpuArchitecture.Arm64, null, CheckStatus.Pass)]
    [InlineData(CpuArchitecture.X86, true, CheckStatus.Pass)]
    [InlineData(CpuArchitecture.X86, false, CheckStatus.Fail)]
    [InlineData(CpuArchitecture.X86, null, CheckStatus.Unknown)]
    [InlineData(CpuArchitecture.Arm, null, CheckStatus.Fail)]
    [InlineData(CpuArchitecture.Unknown, true, CheckStatus.Pass)]
    [InlineData(CpuArchitecture.Unknown, null, CheckStatus.Unknown)]
    public void Architecture_NeedsA64BitProcessor(CpuArchitecture architecture, bool? sixtyFourBit, CheckStatus expected)
    {
        var info = new TargetPcInfo { Cpu = new CpuInfo { Architecture = architecture, Is64BitCapable = sixtyFourBit } };

        var check = Check(info, Windows11Requirement.Architecture);

        Assert.Equal(expected, check.Status);
        // No setup switch turns a 32-bit processor into a 64-bit one.
        Assert.Null(check.Bypass);
    }

    [Fact]
    public void Architecture_NoCpuData_IsUnknown()
    {
        Assert.Equal(CheckStatus.Unknown, Check(new TargetPcInfo(), Windows11Requirement.Architecture).Status);
    }

    [Fact]
    public void Firmware_BiosFailsBothFirmwareAndSecureBoot_ButWithOneBypass()
    {
        var info = new TargetPcInfo { Firmware = new FirmwareInfo { Type = FirmwareType.Bios } };

        Assert.Equal(CheckStatus.Fail, Check(info, Windows11Requirement.Firmware).Status);
        Assert.Equal(CheckStatus.Fail, Check(info, Windows11Requirement.SecureBoot).Status);
        Assert.Equal([LabConfigBypass.SecureBoot], TargetPcAdvisor.Evaluate(info).Bypasses);
    }

    [Theory]
    [InlineData(SecureBootState.On, AdvisorKeys.SecureBootPassOn)]
    [InlineData(SecureBootState.Off, AdvisorKeys.SecureBootPassOff)]
    [InlineData(SecureBootState.Unknown, AdvisorKeys.SecureBootUnknown)]
    public void SecureBoot_OnAndOffBothProveSupport(SecureBootState state, string key)
    {
        var info = new TargetPcInfo { Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = state } };

        Assert.Equal(key, Check(info, Windows11Requirement.SecureBoot).Message.Key);
    }

    [Fact]
    public void Firmware_ArmWithoutFirmwareType_IsUefi()
    {
        var info = new TargetPcInfo { Cpu = new CpuInfo { Architecture = CpuArchitecture.Arm64 }, Firmware = new FirmwareInfo() };

        Assert.Equal(CheckStatus.Pass, Check(info, Windows11Requirement.Firmware).Status);
    }

    [Theory]
    [InlineData(true, "2.0", true, AdvisorKeys.TpmPass, CheckStatus.Pass)]
    [InlineData(true, "2.0", null, AdvisorKeys.TpmPass, CheckStatus.Pass)]
    [InlineData(true, "2.0", false, AdvisorKeys.TpmFailDisabled, CheckStatus.Fail)]
    [InlineData(true, "1.2", true, AdvisorKeys.TpmFailVersion, CheckStatus.Fail)]
    [InlineData(true, null, true, AdvisorKeys.TpmUnknown, CheckStatus.Unknown)]
    [InlineData(false, null, null, AdvisorKeys.TpmFailMissing, CheckStatus.Fail)]
    [InlineData(null, null, null, AdvisorKeys.TpmUnknown, CheckStatus.Unknown)]
    public void Tpm_VersionAndEnabledStateDecide(bool? present, string? version, bool? enabled, string key, CheckStatus expected)
    {
        var info = new TargetPcInfo { Tpm = new TpmInfo { Present = present, SpecVersion = version, IsEnabled = enabled } };

        var check = Check(info, Windows11Requirement.Tpm);

        Assert.Equal(expected, check.Status);
        Assert.Equal(key, check.Message.Key);
        Assert.Equal(expected == CheckStatus.Fail ? LabConfigBypass.Tpm : null, check.Bypass);
    }

    [Fact]
    public void Tpm_NoData_IsUnknown()
    {
        Assert.Equal(CheckStatus.Unknown, Check(new TargetPcInfo(), Windows11Requirement.Tpm).Status);
    }

    [Fact]
    public void CpuModel_IsNeverClaimedAsChecked()
    {
        var modern = Check(TargetPcProfiles.ModernLaptop(), Windows11Requirement.CpuModel);
        var old = Check(TargetPcProfiles.All["Core2DuoBios"], Windows11Requirement.CpuModel);

        Assert.Equal(CheckStatus.NotChecked, modern.Status);
        Assert.Equal(CheckStatus.Fail, old.Status);
        Assert.Equal(LabConfigBypass.Cpu, old.Bypass);
    }

    [Fact]
    public void Graphics_IsReportedAsNotChecked()
    {
        Assert.Equal(CheckStatus.NotChecked, Check(TargetPcProfiles.ModernLaptop(), Windows11Requirement.Graphics).Status);
    }

    [Fact]
    public void Verdict_NotCheckedRequirementsDoNotBlockLikelySupported()
    {
        var checks = Windows11Requirements.Evaluate(TargetPcProfiles.ModernLaptop());

        Assert.Contains(checks, c => c.Status == CheckStatus.NotChecked);
        Assert.Equal(Windows11Verdict.LikelySupported, Windows11Requirements.Verdict(checks));
    }

    [Fact]
    public void Verdict_FailureWithoutBypassOutweighsBypassableOnes()
    {
        var checks = new[]
        {
            new RequirementCheck(Windows11Requirement.Tpm, CheckStatus.Fail, new AdvisorMessage("x", AdvisorSeverity.Warning), LabConfigBypass.Tpm),
            new RequirementCheck(Windows11Requirement.Architecture, CheckStatus.Fail, new AdvisorMessage("y", AdvisorSeverity.Critical)),
        };

        Assert.Equal(Windows11Verdict.NotPossible, Windows11Requirements.Verdict(checks));
    }
}
