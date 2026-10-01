// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;
using Bootrix.Core.Workshop.Hardware;
using static Bootrix.Core.Workshop.Advice.AdvisorKeys;

namespace Bootrix.Core.Tests.Workshop;

/// <summary>What the advisor must conclude for one hardware profile.</summary>
internal sealed record Expectation
{
    public Windows11Verdict Verdict { get; init; }

    public LabConfigBypass[] Bypasses { get; init; } = [];

    /// <summary>Null when no medium fits the hardware.</summary>
    public WindowsProduct? Product { get; init; }

    public string? Version { get; init; }

    public CpuArchitecture? Architecture { get; init; }

    public string? Edition { get; init; }

    public EditionSource EditionSource { get; init; }

    public string? CatalogQuery { get; init; }

    public TargetFirmware Firmware { get; init; } = TargetFirmware.Auto;

    public PartitionScheme Scheme { get; init; } = PartitionScheme.Auto;

    public FileSystemKind FileSystem { get; init; } = FileSystemKind.Auto;

    public BootCertificate Certificate { get; init; } = BootCertificate.Auto;

    public DriverNeedKind[] Drivers { get; init; } = [];

    public string[] Has { get; init; } = [];

    public string[] Lacks { get; init; } = [];
}

public class TargetPcAdvisorTests
{
    private static readonly Dictionary<string, Expectation> Expectations = new()
    {
        ["ModernLaptop"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [LicensingEditionFromFirmware, BootSecureBootSignedOnly],
            Lacks = [DataNotElevated, DataIncomplete, BitLockerActive, BitLockerLocked, BitLockerUnknown, BypassUnsupported, Windows10EndOfSupport, MachineVirtual],
        },
        ["Core2DuoBios"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Cpu, LabConfigBypass.SecureBoot, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows10, Version = "22H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 10 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows10/22H2/en-US/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [CpuNoModernInstructions, Windows10EndOfSupport, LicensingNoFirmwareKey, LicensingEditionFromRunning, LifecycleRunningVersionEnding, BootBiosMode],
            Lacks = [BypassUnsupported],
        },
        ["RyzenWithoutTpm"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Has = [BypassUnsupported, LicensingNoFirmwareKey, LicensingEditionFromRunning],
            Lacks = [CpuNoModernInstructions, Windows10EndOfSupport],
        },
        ["VmdNotebook"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Drivers = [DriverNeedKind.IntelVmd],
            Has = [BitLockerActive],
        },
        ["RstRaidDesktop"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Cpu, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Drivers = [DriverNeedKind.IntelRstRaid],
            Has = [BypassUnsupported],
        },
        ["SurfaceOnArm"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.Arm64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/arm64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [CpuArm64],
            Lacks = [CpuNewPlatform, CpuNoModernInstructions],
        },
        ["SnapdragonX2"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "26H1", Architecture = CpuArchitecture.Arm64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/26H1/de-DE/arm64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [CpuArm64, CpuNewPlatform],
        },
        ["HyperVGeneration2"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [MachineVirtual],
            Lacks = [MachineHyperVGeneration1],
        },
        ["HyperVGeneration1"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.SecureBoot, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [MachineVirtual, MachineHyperVGeneration1, BypassUnsupported],
        },
        ["VmwareBiosSmallDisk"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Ram, LabConfigBypass.SecureBoot, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Drivers = [DriverNeedKind.NetworkVirtual],
            Has = [MachineVirtual],
        },
        ["KvmWithVirtio"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Drivers = [DriverNeedKind.VirtualStorage, DriverNeedKind.NetworkVirtual],
            Has = [MachineVirtual],
        },
        ["XeonServerWithPerc"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = null, EditionSource = EditionSource.None, CatalogQuery = "windows11/25H2/en-US/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Drivers = [DriverNeedKind.HardwareRaid],
            Has = [MachineServerHardware, MachineRunningServer],
            Lacks = [LifecycleRunningVersionEnding],
        },
        ["AtomNetbook32Bit"] = new()
        {
            Verdict = Windows11Verdict.NotPossible,
            Bypasses = [LabConfigBypass.Cpu, LabConfigBypass.Ram, LabConfigBypass.SecureBoot, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows10, Version = "22H2", Architecture = CpuArchitecture.X86,
            Edition = "Windows 10 Home", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows10/22H2/de-DE/x86",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [CpuOnly32Bit, Windows10EndOfSupport],
            Lacks = [CpuNoModernInstructions, BypassUnsupported],
        },
        ["IvyBridgeWith32BitWindows"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Cpu, LabConfigBypass.SecureBoot, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [CpuRunning32Bit, BypassUnsupported],
            Lacks = [CpuOnly32Bit],
        },
        ["RyzenDesktopSecureBootOff"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Has = [LicensingNoFirmwareKey, LicensingEditionFromRunning],
            Lacks = [BootSecureBootSignedOnly],
        },
        ["CoffeeLakeLaptopOnWindows10"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [LicensingEditionFromFirmware, LifecycleRunningVersionEnding],
        },
        ["CoffeeLakeDesktopInCsmMode"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.SecureBoot],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [BypassUnsupported],
            Lacks = [BootBiosLargeDisk],
        },
        ["AtomTabletWithEmmc"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Ram, LabConfigBypass.Storage, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home Single Language", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Has = [BypassUnsupported],
        },
        ["SingleVcpuVm"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Cpu],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [MachineVirtual],
        },
        ["PhenomIi"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Cpu, LabConfigBypass.SecureBoot, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows10, Version = "22H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 10 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows10/22H2/en-US/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [CpuNoModernInstructions, Windows10EndOfSupport],
        },
        ["IvyBridgeDesktop"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.Cpu, LabConfigBypass.Tpm],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Has = [BypassUnsupported],
            Lacks = [CpuNoModernInstructions],
        },
        ["NothingKnown"] = new()
        {
            Verdict = Windows11Verdict.Unknown,
            Has = [DataNothing],
            Lacks = [DataNotElevated, BitLockerUnknown],
        },
        ["NotElevated"] = new()
        {
            Verdict = Windows11Verdict.Unknown,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Has = [DataIncomplete, DataNotElevated, BitLockerUnknown],
            Lacks = [BypassUnsupported],
        },
        ["HomeKeyInFirmware"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [LicensingEditionFromFirmware],
            Lacks = [LicensingEditionFromRunning],
        },
        ["WorkstationKeyInFirmware"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro for Workstations", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [LicensingEditionFromFirmware],
        },
        ["KeyWithoutReadableEdition"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [LicensingEditionUnknown, LicensingEditionFromRunning],
            Lacks = [LicensingEditionFromFirmware],
        },
        ["DbxRevokesPca2011"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Certificate = BootCertificate.Windows2023,
        },
        ["DbWithoutCa2023"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Certificate = BootCertificate.Windows2011,
        },
        ["LockedBitLockerVolume"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Has = [BitLockerLocked, BitLockerActive],
        },
        ["AdaptersWithoutInboxDriver"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
            Drivers = [DriverNeedKind.NetworkVendorDriver, DriverNeedKind.NetworkNoDriver, DriverNeedKind.NetworkVendorDriver],
        },
        ["AmdRaidMode"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.RunningSystem, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt,
            Drivers = [DriverNeedKind.AmdRaid],
        },
        ["WindowsPeSession"] = new()
        {
            Verdict = Windows11Verdict.LikelySupported,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Home", EditionSource = EditionSource.FirmwareKey, CatalogQuery = null,
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
        },
        ["LargeDiskOnBios"] = new()
        {
            Verdict = Windows11Verdict.NeedsBypass,
            Bypasses = [LabConfigBypass.SecureBoot],
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr,
            Has = [BootBiosLargeDisk],
        },
        ["BorderlineMemoryAndDisk"] = new()
        {
            Verdict = Windows11Verdict.Unknown,
            Product = WindowsProduct.Windows11, Version = "25H2", Architecture = CpuArchitecture.X64,
            Edition = "Windows 11 Pro", EditionSource = EditionSource.FirmwareKey, CatalogQuery = "windows11/25H2/de-DE/x64",
            Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Fat32,
        },
    };

    public static TheoryData<string> ProfileNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in TargetPcProfiles.All.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void EveryProfileHasAnExpectationAndTheOtherWayRound()
    {
        Assert.Equal(TargetPcProfiles.All.Keys.Order(), Expectations.Keys.Order());
        Assert.True(TargetPcProfiles.All.Count >= 25, "the table must cover at least 25 hardware profiles");
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Profile_ProducesTheExpectedAdvice(string name)
    {
        var expected = Expectations[name];

        var assessment = TargetPcAdvisor.Evaluate(TargetPcProfiles.All[name]);

        Assert.Equal(expected.Verdict, assessment.Windows11);
        Assert.Equal(expected.Bypasses, assessment.Bypasses);
        Assert.Equal(expected.Drivers, assessment.DriverNeeds.Select(d => d.Kind));

        if (expected.Product is null)
        {
            Assert.Null(assessment.Image);
        }
        else
        {
            var image = assessment.Image;
            Assert.NotNull(image);
            Assert.Equal(expected.Product, image.Product);
            Assert.Equal(expected.Version, image.Version);
            Assert.Equal(expected.Architecture, image.Architecture);
            Assert.Equal(expected.Edition, image.Edition);
            Assert.Equal(expected.EditionSource, image.EditionSource);
            Assert.Equal(expected.CatalogQuery, image.CatalogQuery);
        }

        Assert.Equal(expected.Firmware, assessment.Boot.Firmware);
        Assert.Equal(expected.Scheme, assessment.Boot.Scheme);
        Assert.Equal(expected.FileSystem, assessment.Boot.FileSystem);
        Assert.Equal(expected.Certificate, assessment.Boot.Certificate);

        var keys = assessment.Messages.Select(m => m.Key).Concat(assessment.Boot.Notes.Select(m => m.Key)).ToList();
        foreach (var key in expected.Has)
        {
            Assert.Contains(key, keys);
        }

        foreach (var key in expected.Lacks)
        {
            Assert.DoesNotContain(key, keys);
        }
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Profile_SuggestedJobFollowsTheAssessment(string name)
    {
        var info = TargetPcProfiles.All[name];
        var assessment = TargetPcAdvisor.Evaluate(info);
        var job = assessment.SuggestedProfile;

        Assert.Equal(JobKind.WriteImage, job.Kind);
        Assert.Equal(assessment.Boot.Scheme, job.Target.Scheme);
        Assert.Equal(assessment.Boot.Firmware, job.Target.Firmware);
        Assert.Equal(assessment.Boot.FileSystem, job.Target.FileSystem);
        Assert.Equal(assessment.Boot.Certificate, job.Windows.BootCertificate);
        Assert.Equal(assessment.Image?.CatalogQuery, job.Source?.CatalogQuery);
        Assert.Equal(assessment.Image?.Edition, job.Windows.Edition);

        // Bypass flags exist for Windows 11 Setup only.
        var windows11 = assessment.Image?.Product == WindowsProduct.Windows11;
        Assert.Equal(windows11 && assessment.Bypasses.Contains(LabConfigBypass.Tpm), job.Windows.BypassTpm);
        Assert.Equal(windows11 && assessment.Bypasses.Contains(LabConfigBypass.SecureBoot), job.Windows.BypassSecureBoot);
        Assert.Equal(windows11 && assessment.Bypasses.Contains(LabConfigBypass.Ram), job.Windows.BypassRam);
        Assert.Equal(windows11 && assessment.Bypasses.Contains(LabConfigBypass.Cpu), job.Windows.BypassCpu);
        Assert.Equal(windows11 && assessment.Bypasses.Contains(LabConfigBypass.Storage), job.Windows.BypassStorage);

        // The job never carries a target device, a password or an account: those are decisions of the technician.
        Assert.Null(job.Windows.LocalAccountName);
        Assert.Empty(job.Windows.DriverFolders);
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Profile_EveryMessageHasGermanAndEnglishText(string name)
    {
        var assessment = TargetPcAdvisor.Evaluate(TargetPcProfiles.All[name]);
        var messages = assessment.Messages
            .Concat(assessment.Windows11Checks.Select(c => c.Message))
            .Concat(assessment.DriverNeeds.Select(d => d.Message))
            .Concat(assessment.Boot.Notes)
            .Concat(assessment.Image?.Alternatives ?? []);

        foreach (var message in messages)
        {
            foreach (var culture in new[] { "de", "en" })
            {
                var localizer = new Bootrix.Core.Localization.Localizer { Culture = System.Globalization.CultureInfo.GetCultureInfo(culture) };
                var text = message.Describe(localizer);

                Assert.NotEqual(message.Key, text);
                Assert.DoesNotContain("{0", text, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Profile_EvaluationIsDeterministic(string name)
    {
        var info = TargetPcProfiles.All[name];

        Assert.Equal(WorkshopJson.Serialize(TargetPcAdvisor.Evaluate(info)), WorkshopJson.Serialize(TargetPcAdvisor.Evaluate(info)));
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Profile_ChecksAreListedOncePerRequirementInOrder(string name)
    {
        var checks = TargetPcAdvisor.Evaluate(TargetPcProfiles.All[name]).Windows11Checks;

        Assert.Equal(Enum.GetValues<Windows11Requirement>(), checks.Select(c => c.Requirement));
    }

    [Fact]
    public void Messages_AreOrderedBySeverity()
    {
        var assessment = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["LockedBitLockerVolume"]);

        Assert.Equal(assessment.Messages.OrderByDescending(m => m.Severity).Select(m => m.Key), assessment.Messages.Select(m => m.Key));
        Assert.Equal(AdvisorSeverity.Critical, assessment.Messages[0].Severity);
    }

    [Fact]
    public void Evaluate_TodayDefaultsToTheCollectionDate()
    {
        var info = TargetPcProfiles.ModernLaptop() with { CollectedAt = new DateTimeOffset(2027, 1, 15, 23, 30, 0, TimeSpan.Zero) };

        Assert.Equal(new DateOnly(2027, 1, 15), TargetPcAdvisor.Evaluate(info).AssessedOn);
        Assert.Equal(new DateOnly(2030, 5, 1), TargetPcAdvisor.Evaluate(info, new TargetPcAdvisorOptions { Today = new DateOnly(2030, 5, 1) }).AssessedOn);
    }

    [Fact]
    public void Evaluate_ReleaseChoiceFollowsTheDate()
    {
        var info = TargetPcProfiles.ModernLaptop();

        // 26H2 became available two days before the profile's collection date, so it has not settled yet.
        Assert.Equal("25H2", TargetPcAdvisor.Evaluate(info).Image!.Version);
        Assert.Equal("26H2", TargetPcAdvisor.Evaluate(info, new TargetPcAdvisorOptions { Today = new DateOnly(2026, 11, 15) }).Image!.Version);
        Assert.Equal("24H2", TargetPcAdvisor.Evaluate(info, new TargetPcAdvisorOptions { Today = new DateOnly(2025, 6, 1) }).Image!.Version);
    }

    [Fact]
    public void Evaluate_NullInfo_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TargetPcAdvisor.Evaluate(null!));
    }

    [Fact]
    public void LegacyBiosMessage_NamesTheOnlyAlternativesForAnOldPc()
    {
        var image = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["Core2DuoBios"]).Image!;

        Assert.Equal([AlternativeLinux], image.Alternatives.Select(a => a.Key));
    }

    [Fact]
    public void BypassedWindows11_OffersWindows10AndLinuxAsSupportedWays()
    {
        var image = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["IvyBridgeDesktop"]).Image!;

        Assert.Equal([AlternativeWindows10, AlternativeLinux], image.Alternatives.Select(a => a.Key));
    }

    [Fact]
    public void FirmwareKeyChannelIsPassedOnForEiCfg()
    {
        var image = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["ModernLaptop"]).Image!;

        Assert.Equal("Professional", image.EditionId);
        Assert.Equal("OEM", image.Channel);
        Assert.Equal(new DateOnly(2027, 10, 12), image.EndOfServicing);
    }

    [Fact]
    public void WindowsPe_DoesNotPrefillLanguageOrTimeZone()
    {
        var job = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["WindowsPeSession"]).SuggestedProfile;

        Assert.Null(job.Windows.UiLanguage);
        Assert.Null(job.Windows.TimeZone);
    }

    [Fact]
    public void RunningSystem_PrefillsLanguageAndTimeZone()
    {
        var job = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["ModernLaptop"]).SuggestedProfile;

        Assert.Equal("de-DE", job.Windows.UiLanguage);
        Assert.Equal("W. Europe Standard Time", job.Windows.TimeZone);
        Assert.Equal("Dell Inc. Latitude 5440", job.Name);
    }

    [Fact]
    public void Driver_NeedsCarryHardwareIdAndStage()
    {
        var needs = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["VmdNotebook"]).DriverNeeds;

        var vmd = Assert.Single(needs);
        Assert.Equal(DriverStage.Setup, vmd.Stage);
        Assert.Equal(@"PCI\VEN_8086&DEV_467F", vmd.HardwareId);
        Assert.Equal(AdvisorSeverity.Critical, vmd.Message.Severity);
        Assert.Equal(DriverIntelVmd, vmd.Message.Key);
        Assert.Contains("467F", (string)vmd.Message.Arguments[1]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_WirelessAndWiredNeedsAreTold_Apart()
    {
        var needs = TargetPcAdvisor.Evaluate(TargetPcProfiles.All["AdaptersWithoutInboxDriver"]).DriverNeeds;

        Assert.Equal([DriverNetworkVendor, DriverWirelessNoDriver, DriverNetworkVendor], needs.Select(n => n.Message.Key));
        Assert.Equal([false, true, false], needs.Select(n => n.IsWireless));
        Assert.All(needs, n => Assert.Equal(DriverStage.FirstBoot, n.Stage));
    }

    [Fact]
    public void Driver_TheSameControllerSeenTwice_IsListedOnce()
    {
        var info = TargetPcProfiles.All["VmdNotebook"];
        var twice = info with { Devices = [.. info.Devices!, .. info.Devices!] };

        Assert.Single(TargetPcAdvisor.Evaluate(twice).DriverNeeds);
    }

    [Fact]
    public void NoDevicesAtAll_MeansNoDriverNeeds()
    {
        Assert.Empty(TargetPcAdvisor.Evaluate(TargetPcProfiles.ModernLaptop() with { Devices = null }).DriverNeeds);
    }

    [Fact]
    public void Features_FromEmulation_AreNotJudged()
    {
        // The collector reports null features when the process is emulated; absence of data must not read as absence of POPCNT.
        var info = TargetPcProfiles.ModernLaptop() with { Cpu = TargetPcProfiles.ModernLaptop().Cpu! with { Features = null } };

        var assessment = TargetPcAdvisor.Evaluate(info);

        Assert.DoesNotContain(assessment.Messages, m => m.Key == CpuNoModernInstructions);
        Assert.Equal(WindowsProduct.Windows11, assessment.Image!.Product);
    }

    [Fact]
    public void NoInternalDisk_IsReportedAsAPossibleMissingStorageDriver()
    {
        var assessment = TargetPcAdvisor.Evaluate(TargetPcProfiles.ModernLaptop() with { Disks = [] });

        Assert.Contains(assessment.Messages, m => m.Key == StorageNoDisk);
        Assert.Equal(CheckStatus.Unknown, assessment.Windows11Checks.Single(c => c.Requirement == Windows11Requirement.Storage).Status);
    }

    [Fact]
    public void Arm32Device_GetsNoMediumAndAClearStatement()
    {
        var info = TargetPcProfiles.ModernLaptop() with
        {
            Cpu = new CpuInfo { Architecture = CpuArchitecture.Arm, Is64BitCapable = false, PhysicalCores = 4, LogicalProcessors = 4, MaxClockMhz = 1200 },
        };

        var assessment = TargetPcAdvisor.Evaluate(info);

        Assert.Equal(Windows11Verdict.NotPossible, assessment.Windows11);
        Assert.Null(assessment.Image);
        Assert.Null(assessment.SuggestedProfile.Source);
        Assert.Contains(assessment.Messages, m => m.Key == CpuArm32 && m.Severity == AdvisorSeverity.Critical);
    }

    [Fact]
    public void LegacyBootDecision_DoesNotDependOnTheMedium()
    {
        // Even when no medium fits, the boot recommendation still describes how this firmware starts.
        var info = TargetPcProfiles.All["AtomNetbook32Bit"];

        Assert.Equal(TargetFirmware.Bios, TargetPcAdvisor.Evaluate(info).Boot.Firmware);
    }

    [Fact]
    public void Edition_FromServerRunningSystem_IsNeverSuggested()
    {
        var info = TargetPcProfiles.All["XeonServerWithPerc"];

        var image = TargetPcAdvisor.Evaluate(info).Image!;

        Assert.Null(image.Edition);
        Assert.Null(image.EditionId);
    }
}
