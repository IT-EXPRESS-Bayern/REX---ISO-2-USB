// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

/// <summary>Hardware as the collector would report it, for machines a workshop meets. Each builder returns a complete profile.</summary>
internal static class TargetPcProfiles
{
    public static readonly DateTimeOffset CollectedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public const long GiB = 1L << 30;

    private const CpuFeatures ModernX86 =
        CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Ssse3 | CpuFeatures.Sse41 | CpuFeatures.Sse42 | CpuFeatures.Popcnt
        | CpuFeatures.Avx | CpuFeatures.Avx2 | CpuFeatures.Bmi1 | CpuFeatures.Bmi2 | CpuFeatures.Fma | CpuFeatures.Lzcnt | CpuFeatures.Aes;

    private const CpuFeatures ArmFeatures = CpuFeatures.ArmAdvSimd | CpuFeatures.ArmAes | CpuFeatures.ArmCrc32 | CpuFeatures.ArmSha1 | CpuFeatures.ArmSha256;

    public static IReadOnlyDictionary<string, TargetPcInfo> All { get; } = new Dictionary<string, TargetPcInfo>
    {
        ["ModernLaptop"] = ModernLaptop(),
        ["Core2DuoBios"] = Core2DuoBios(),
        ["RyzenWithoutTpm"] = RyzenWithoutTpm(),
        ["VmdNotebook"] = VmdNotebook(),
        ["RstRaidDesktop"] = RstRaidDesktop(),
        ["SurfaceOnArm"] = SurfaceOnArm(),
        ["SnapdragonX2"] = SnapdragonX2(),
        ["HyperVGeneration2"] = HyperVGeneration2(),
        ["HyperVGeneration1"] = HyperVGeneration1(),
        ["VmwareBiosSmallDisk"] = VmwareBiosSmallDisk(),
        ["KvmWithVirtio"] = KvmWithVirtio(),
        ["XeonServerWithPerc"] = XeonServerWithPerc(),
        ["AtomNetbook32Bit"] = AtomNetbook32Bit(),
        ["IvyBridgeWith32BitWindows"] = IvyBridgeWith32BitWindows(),
        ["RyzenDesktopSecureBootOff"] = RyzenDesktopSecureBootOff(),
        ["CoffeeLakeLaptopOnWindows10"] = CoffeeLakeLaptopOnWindows10(),
        ["CoffeeLakeDesktopInCsmMode"] = CoffeeLakeDesktopInCsmMode(),
        ["AtomTabletWithEmmc"] = AtomTabletWithEmmc(),
        ["SingleVcpuVm"] = SingleVcpuVm(),
        ["PhenomIi"] = PhenomIi(),
        ["IvyBridgeDesktop"] = IvyBridgeDesktop(),
        ["NothingKnown"] = new TargetPcInfo { CollectedAt = CollectedAt },
        ["NotElevated"] = NotElevated(),
        ["HomeKeyInFirmware"] = HomeKeyInFirmware(),
        ["WorkstationKeyInFirmware"] = WorkstationKeyInFirmware(),
        ["KeyWithoutReadableEdition"] = KeyWithoutReadableEdition(),
        ["DbxRevokesPca2011"] = DbxRevokesPca2011(),
        ["DbWithoutCa2023"] = DbWithoutCa2023(),
        ["LockedBitLockerVolume"] = LockedBitLockerVolume(),
        ["AdaptersWithoutInboxDriver"] = AdaptersWithoutInboxDriver(),
        ["AmdRaidMode"] = AmdRaidMode(),
        ["WindowsPeSession"] = WindowsPeSession(),
        ["LargeDiskOnBios"] = LargeDiskOnBios(),
        ["BorderlineMemoryAndDisk"] = BorderlineMemoryAndDisk(),
    };

    public static TargetPcInfo ModernLaptop() => new()
    {
        CollectedAt = CollectedAt,
        IsElevated = true,
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i7-1355U", physical: 10, logical: 12, mhz: 1700),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.On, Vendor = "Dell Inc.", Version = "1.12.0", ReleaseDate = new DateOnly(2024, 5, 1) },
        Machine = new MachineIdentity { Manufacturer = "Dell Inc.", Model = "Latitude 5440", SerialNumber = "ABC1234", ChassisType = 10, VirtualMachine = VirtualMachineKind.None },
        Tpm = new TpmInfo { Present = true, SpecVersion = "2.0", IsEnabled = true, IsActivated = true },
        Memory = new MemoryInfo { InstalledBytes = 16 * GiB, UsableBytes = (long)(15.6 * GiB) },
        Disks = [Disk(0, "Samsung SSD 970 EVO", 512_110_190_592, BusType.Nvme, DiskMediaType.Ssd, system: true)],
        Devices = [StandardNvme(), IntelWifi("2723")],
        BitLocker = [new BitLockerVolumeInfo { Volume = "C:", Protection = BitLockerProtection.Off, Conversion = BitLockerConversion.FullyDecrypted, IsLocked = false }],
        OemLicense = Key("Professional"),
        RunningSystem = Windows11("Professional"),
    };

    private static TargetPcInfo Core2DuoBios() => new()
    {
        CollectedAt = CollectedAt,
        IsElevated = true,
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM)2 Duo CPU     E8400  @ 3.00GHz", physical: 2, logical: 2, mhz: 3000,
            features: CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Ssse3 | CpuFeatures.Sse41),
        Firmware = new FirmwareInfo { Type = FirmwareType.Bios, SecureBoot = SecureBootState.Unknown, ReleaseDate = new DateOnly(2010, 6, 1) },
        Machine = new MachineIdentity { Manufacturer = "Dell Inc.", Model = "OptiPlex 760", ChassisType = 6, VirtualMachine = VirtualMachineKind.None },
        Tpm = new TpmInfo { Present = false },
        Memory = new MemoryInfo { InstalledBytes = 4 * GiB, UsableBytes = (long)(3.9 * GiB) },
        Disks = [Disk(0, "WDC WD2500AAKS", 250_059_350_016, BusType.Sata, DiskMediaType.Hdd, system: true, style: DiskPartitionStyle.Mbr)],
        Devices = [Pci("Intel(R) ICH10 SATA AHCI Controller", "8086", "3A22", "010601", "storahci", "SCSIAdapter")],
        BitLocker = [],
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
        RunningSystem = Windows10("Professional", language: "en-US"),
    };

    private static TargetPcInfo RyzenWithoutTpm() => ModernLaptop() with
    {
        Cpu = X64("AuthenticAMD", "AMD Ryzen 5 3600 6-Core Processor", physical: 6, logical: 12, mhz: 3600),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Off },
        Machine = new MachineIdentity { BoardManufacturer = "ASUSTeK COMPUTER INC.", BoardModel = "PRIME B450M-A", ChassisType = 3, VirtualMachine = VirtualMachineKind.None },
        Tpm = new TpmInfo { Present = false },
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
        RunningSystem = Windows10("Core"),
    };

    private static TargetPcInfo VmdNotebook() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "12th Gen Intel(R) Core(TM) i5-1245U", physical: 10, logical: 12, mhz: 1600),
        Disks = [Disk(0, "KBG50ZNS256G KIOXIA", 256_060_514_304, BusType.RAID, DiskMediaType.Ssd, system: true)],
        Devices =
        [
            Pci("Intel(R) RST VMD Controller 467F", "8086", "467F", "010400", "iaStorVD", "SCSIAdapter", driver: Vendor("oem42.inf", "Intel Corporation", "19.5.2.1049")),
            IntelWifi("51F0"),
        ],
        BitLocker = [new BitLockerVolumeInfo { Volume = "C:", Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted, IsLocked = false, EncryptionMethod = "XtsAes128" }],
    };

    private static TargetPcInfo RstRaidDesktop() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", physical: 4, logical: 8, mhz: 4000),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Off },
        Machine = new MachineIdentity { Manufacturer = "Gigabyte", Model = "Z170X-Gaming 5", ChassisType = 3, VirtualMachine = VirtualMachineKind.None },
        Tpm = new TpmInfo { Present = false },
        Disks = [Disk(0, "Intel Raid 0 Volume", 1_000_000_000_000, BusType.RAID, DiskMediaType.Ssd, system: true)],
        Devices = [Pci("Intel(R) Chipset SATA/PCIe RST Premium Controller", "8086", "A106", "010400", "iaStorAC", "SCSIAdapter", driver: Vendor("oem7.inf", "Intel Corporation", "17.9.0.1000"))],
        BitLocker = [],
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
        RunningSystem = Windows10("Professional"),
    };

    private static TargetPcInfo SurfaceOnArm() => ModernLaptop() with
    {
        Cpu = new CpuInfo
        {
            Vendor = "Qualcomm Technologies Inc",
            Name = "Snapdragon(R) X Plus - X1P64100 - Qualcomm(R) Oryon(TM) CPU",
            Architecture = CpuArchitecture.Arm64,
            Is64BitCapable = true,
            PhysicalCores = 10,
            LogicalProcessors = 10,
            MaxClockMhz = 3400,
            Features = ArmFeatures,
        },
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.On },
        Machine = new MachineIdentity { Manufacturer = "Microsoft Corporation", Model = "Surface Pro, 11th Edition", ChassisType = 31, VirtualMachine = VirtualMachineKind.None },
        Disks = [Disk(0, "Micron MTFDKBA256", 256_060_514_304, BusType.Nvme, DiskMediaType.Ssd, system: true)],
        Devices = [StandardNvme()],
        OemLicense = Key("Core"),
        RunningSystem = Windows11("Core") with { Architecture = CpuArchitecture.Arm64 },
    };

    private static TargetPcInfo SnapdragonX2() => SurfaceOnArm() with
    {
        Cpu = new CpuInfo
        {
            Vendor = "Qualcomm Technologies Inc",
            Name = "Snapdragon(R) X2 Elite Extreme - X2E-96-100 - Qualcomm(R) Oryon(TM) CPU",
            Architecture = CpuArchitecture.Arm64,
            Is64BitCapable = true,
            PhysicalCores = 18,
            LogicalProcessors = 18,
            MaxClockMhz = 4400,
            Features = ArmFeatures,
        },
    };

    private static TargetPcInfo HyperVGeneration2() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Xeon(R) Platinum 8272CL CPU @ 2.60GHz", physical: 4, logical: 4, mhz: 2600, hypervisor: "Microsoft Hv"),
        Machine = new MachineIdentity { Manufacturer = "Microsoft Corporation", Model = "Virtual Machine", ChassisType = 3, VirtualMachine = VirtualMachineKind.HyperV },
        Memory = new MemoryInfo { InstalledBytes = 4 * GiB, UsableBytes = (long)(3.9 * GiB) },
        Disks = [Disk(0, "Msft Virtual Disk", 127 * GiB, BusType.Sas, DiskMediaType.Unknown, system: true)],
        Devices = [StandardNvme()],
        BitLocker = [],
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
    };

    private static TargetPcInfo HyperVGeneration1() => HyperVGeneration2() with
    {
        Firmware = new FirmwareInfo { Type = FirmwareType.Bios },
        Tpm = new TpmInfo { Present = false },
    };

    private static TargetPcInfo VmwareBiosSmallDisk() => HyperVGeneration1() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz", physical: 2, logical: 2, mhz: 2600, hypervisor: "VMwareVMware"),
        Machine = new MachineIdentity { Manufacturer = "VMware, Inc.", Model = "VMware7,1", ChassisType = 1, VirtualMachine = VirtualMachineKind.VMware },
        Memory = new MemoryInfo { InstalledBytes = 2 * GiB, UsableBytes = (long)(1.9 * GiB) },
        Disks = [Disk(0, "VMware Virtual NVMe Disk", 60 * GiB, BusType.Nvme, DiskMediaType.Ssd, system: true, style: DiskPartitionStyle.Mbr)],
        Devices = [Pci("VMXNET3 Ethernet Adapter", "15AD", "07B0", "020000", "vmxnet3ndis6", "Net", problem: 28)],
    };

    private static TargetPcInfo KvmWithVirtio() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Xeon(R) Processor", physical: 4, logical: 4, mhz: 2100, hypervisor: "KVMKVMKVM"),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Off },
        Machine = new MachineIdentity { Manufacturer = "QEMU", Model = "Standard PC (Q35 + ICH9, 2009)", ChassisType = 1, VirtualMachine = VirtualMachineKind.QemuKvm },
        Memory = new MemoryInfo { InstalledBytes = 8 * GiB, UsableBytes = (long)(7.9 * GiB) },
        Disks = [Disk(0, "Red Hat VirtIO SCSI Disk Device", 100 * GiB, BusType.Scsi, DiskMediaType.Unknown, system: true)],
        Devices =
        [
            Pci("Red Hat VirtIO SCSI controller", "1AF4", "1048", "010000", "vioscsi", "SCSIAdapter"),
            Pci("Red Hat VirtIO Ethernet Adapter", "1AF4", "1041", "020000", "netkvm", "Net", driver: Vendor("oem2.inf", "Red Hat, Inc.", "100.95.104.26200")),
        ],
        BitLocker = [],
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
    };

    private static TargetPcInfo XeonServerWithPerc() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Xeon(R) Silver 4110 CPU @ 2.10GHz", physical: 16, logical: 32, mhz: 2100),
        Machine = new MachineIdentity { Manufacturer = "Dell Inc.", Model = "PowerEdge R640", ChassisType = 23, VirtualMachine = VirtualMachineKind.None },
        Memory = new MemoryInfo { InstalledBytes = 64 * GiB, UsableBytes = (long)(63.9 * GiB) },
        Disks = [Disk(0, "DELL PERC H730P Mini", 479_559_942_144, BusType.RAID, DiskMediaType.Unknown, system: true)],
        Devices = [Pci("DELL PERC H730P Mini", "1000", "005D", "010400", "megasas2", "SCSIAdapter")],
        BitLocker = [],
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
        RunningSystem = new RunningSystemInfo
        {
            ProductName = "Windows Server 2022 Standard",
            EditionId = "ServerStandard",
            BuildNumber = 20348,
            DisplayVersion = "21H2",
            Architecture = CpuArchitecture.X64,
            IsServer = true,
            IsWinPe = false,
            UiLanguage = "en-US",
            TimeZoneId = "UTC",
        },
    };

    private static TargetPcInfo AtomNetbook32Bit() => Core2DuoBios() with
    {
        Cpu = new CpuInfo
        {
            Vendor = "GenuineIntel",
            Name = "Intel(R) Atom(TM) CPU N270   @ 1.60GHz",
            Architecture = CpuArchitecture.X86,
            Is64BitCapable = false,
            PhysicalCores = 1,
            LogicalProcessors = 2,
            MaxClockMhz = 1600,
            Features = CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Ssse3,
        },
        Machine = new MachineIdentity { Manufacturer = "ASUSTeK Computer INC.", Model = "1005HA", ChassisType = 10, VirtualMachine = VirtualMachineKind.None },
        Memory = new MemoryInfo { InstalledBytes = 1 * GiB, UsableBytes = (long)(0.99 * GiB) },
        Disks = [Disk(0, "ST9160314AS", 160_041_885_696, BusType.Sata, DiskMediaType.Hdd, system: true, style: DiskPartitionStyle.Mbr)],
        RunningSystem = Windows10("Core", language: "de-DE") with { Architecture = CpuArchitecture.X86 },
    };

    private static TargetPcInfo IvyBridgeWith32BitWindows() => Core2DuoBios() with
    {
        Cpu = new CpuInfo
        {
            Vendor = "GenuineIntel",
            Name = "Intel(R) Core(TM) i5-3320M CPU @ 2.60GHz",
            Architecture = CpuArchitecture.X86,
            Is64BitCapable = true,
            PhysicalCores = 2,
            LogicalProcessors = 4,
            MaxClockMhz = 2600,
            Features = CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Ssse3 | CpuFeatures.Sse41 | CpuFeatures.Sse42 | CpuFeatures.Popcnt | CpuFeatures.Avx,
        },
        Memory = new MemoryInfo { InstalledBytes = 8 * GiB, UsableBytes = (long)(3.2 * GiB) },
        RunningSystem = Windows10("Professional") with { Architecture = CpuArchitecture.X86 },
    };

    private static TargetPcInfo RyzenDesktopSecureBootOff() => ModernLaptop() with
    {
        Cpu = X64("AuthenticAMD", "AMD Ryzen 7 7700 8-Core Processor", physical: 8, logical: 16, mhz: 3800),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Off },
        Machine = new MachineIdentity { BoardManufacturer = "ASRock", BoardModel = "B650M Pro RS", ChassisType = 3, VirtualMachine = VirtualMachineKind.None },
        Memory = new MemoryInfo { InstalledBytes = 32 * GiB, UsableBytes = (long)(31.2 * GiB) },
        OemLicense = new OemLicenseInfo { HasFirmwareKey = false },
        RunningSystem = Windows11("Core"),
    };

    private static TargetPcInfo CoffeeLakeLaptopOnWindows10() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", physical: 4, logical: 8, mhz: 1800),
        Memory = new MemoryInfo { InstalledBytes = 8 * GiB, UsableBytes = (long)(7.8 * GiB) },
        Disks = [Disk(0, "SanDisk SD9SN8W256G", 256_060_514_304, BusType.Sata, DiskMediaType.Ssd, system: true)],
        OemLicense = Key("Core"),
        RunningSystem = Windows10("Core"),
    };

    private static TargetPcInfo CoffeeLakeDesktopInCsmMode() => CoffeeLakeLaptopOnWindows10() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz", physical: 6, logical: 6, mhz: 2800),
        Firmware = new FirmwareInfo { Type = FirmwareType.Bios },
        Machine = new MachineIdentity { Manufacturer = "HP", Model = "ProDesk 400 G5 SFF", ChassisType = 4, VirtualMachine = VirtualMachineKind.None },
        Disks = [Disk(0, "Samsung SSD 860", 500_107_862_016, BusType.Sata, DiskMediaType.Ssd, system: true, style: DiskPartitionStyle.Mbr)],
        RunningSystem = Windows10("Professional"),
        OemLicense = Key("Professional"),
    };

    private static TargetPcInfo AtomTabletWithEmmc() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Atom(TM) x5-Z8350  CPU @ 1.44GHz", physical: 4, logical: 4, mhz: 1440, features: CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Ssse3 | CpuFeatures.Sse41 | CpuFeatures.Sse42 | CpuFeatures.Popcnt),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Off },
        Machine = new MachineIdentity { Manufacturer = "Medion", Model = "E1239T", ChassisType = 30, VirtualMachine = VirtualMachineKind.None },
        Tpm = new TpmInfo { Present = false },
        Memory = new MemoryInfo { InstalledBytes = 2 * GiB, UsableBytes = (long)(1.9 * GiB) },
        Disks = [Disk(0, "HBG4a2", 31_276_032_000, BusType.Mmc, DiskMediaType.Ssd, system: true)],
        Devices = [],
        OemLicense = Key("CoreSingleLanguage"),
        RunningSystem = Windows10("CoreSingleLanguage"),
    };

    private static TargetPcInfo SingleVcpuVm() => HyperVGeneration2() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Xeon(R) Platinum 8272CL CPU @ 2.60GHz", physical: 1, logical: 1, mhz: 2600, hypervisor: "Microsoft Hv"),
    };

    private static TargetPcInfo PhenomIi() => Core2DuoBios() with
    {
        Cpu = X64("AuthenticAMD", "AMD Phenom(tm) II X4 955 Processor", physical: 4, logical: 4, mhz: 3200,
            features: CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Popcnt | CpuFeatures.Lzcnt),
        Machine = new MachineIdentity { BoardManufacturer = "Gigabyte", BoardModel = "GA-870A-UD3", ChassisType = 7, VirtualMachine = VirtualMachineKind.None },
    };

    private static TargetPcInfo IvyBridgeDesktop() => ModernLaptop() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i7-3770 CPU @ 3.40GHz", physical: 4, logical: 8, mhz: 3400, features: CpuFeatures.Sse2 | CpuFeatures.Sse3 | CpuFeatures.Ssse3 | CpuFeatures.Sse41 | CpuFeatures.Sse42 | CpuFeatures.Popcnt | CpuFeatures.Avx),
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Off },
        Machine = new MachineIdentity { Manufacturer = "FUJITSU", Model = "ESPRIMO P710", ChassisType = 6, VirtualMachine = VirtualMachineKind.None },
        Tpm = new TpmInfo { Present = false },
        OemLicense = Key("Professional"),
        RunningSystem = Windows10("Professional"),
    };

    private static TargetPcInfo NotElevated() => ModernLaptop() with
    {
        IsElevated = false,
        Firmware = new FirmwareInfo { Type = FirmwareType.Uefi, SecureBoot = SecureBootState.Unknown },
        Tpm = null,
        BitLocker = null,
        Issues = [new CollectionIssue("tpm", "Access denied"), new CollectionIssue("bitlocker", "Access denied")],
    };

    private static TargetPcInfo HomeKeyInFirmware() => CoffeeLakeLaptopOnWindows10() with
    {
        Cpu = X64("GenuineIntel", "Intel(R) Core(TM) i7-8550U CPU @ 1.80GHz", physical: 4, logical: 8, mhz: 1800),
        OemLicense = Key("Core"),
        RunningSystem = Windows10("Professional"),
    };

    private static TargetPcInfo WorkstationKeyInFirmware() => ModernLaptop() with
    {
        OemLicense = Key("ProfessionalWorkstation"),
        RunningSystem = Windows11("ProfessionalWorkstation"),
    };

    private static TargetPcInfo KeyWithoutReadableEdition() => ModernLaptop() with
    {
        OemLicense = new OemLicenseInfo
        {
            HasFirmwareKey = true,
            MaskedKey = "XXXXX-XXXXX-XXXXX-XXXXX-6789N",
            EditionDescription = "[4.0] Something OEM:DM",
            Channel = "OEM",
        },
        RunningSystem = Windows11("Professional"),
    };

    private static TargetPcInfo DbxRevokesPca2011() => ModernLaptop() with
    {
        SecureBootCertificates = new SecureBootCertificateInfo { Windows2023InDb = true, Windows2011InDb = true, Windows2011RevokedInDbx = true },
    };

    private static TargetPcInfo DbWithoutCa2023() => ModernLaptop() with
    {
        SecureBootCertificates = new SecureBootCertificateInfo { Windows2023InDb = false, Windows2011InDb = true, Windows2011RevokedInDbx = false },
    };

    private static TargetPcInfo LockedBitLockerVolume() => ModernLaptop() with
    {
        BitLocker =
        [
            new BitLockerVolumeInfo { Volume = "C:", Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted, IsLocked = false },
            new BitLockerVolumeInfo { Volume = "D:", Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted, IsLocked = true },
        ],
    };

    private static TargetPcInfo AdaptersWithoutInboxDriver() => ModernLaptop() with
    {
        Devices =
        [
            StandardNvme(),
            Pci("Realtek PCIe 2.5GbE Family Controller", "10EC", "8125", "020000", "rt25cx21x64", "Net", driver: Vendor("oem15.inf", "Realtek", "1125.21.1015.2022")),
            Pci("Intel(R) Wi-Fi 7 BE200 320MHz", "8086", "272B", "028000", null, null, problem: 28),
            new HardwareDevice
            {
                InstanceId = @"USB\VID_0BDA&PID_8153\000001000000",
                Name = "Realtek USB GbE Family Controller",
                ClassName = "Net",
                HardwareIds = [@"USB\VID_0BDA&PID_8153&REV_3000", @"USB\VID_0BDA&PID_8153"],
                ProblemCode = 0,
                Driver = Vendor("oem21.inf", "Realtek", "10.66.0.0"),
            },
            Pci("Intel(R) Ethernet Connection I219-V", "8086", "15FA", "020000", "e1dexpress", "Net", driver: Vendor("e1d68x64.inf", "Intel", "12.19.2.45")),
        ],
    };

    private static TargetPcInfo AmdRaidMode() => RyzenDesktopSecureBootOff() with
    {
        Devices = [Pci("AMD SATA Controller", "1022", "7916", "010400", "rcraid", "SCSIAdapter", driver: Vendor("oem9.inf", "Advanced Micro Devices, Inc", "9.3.2.221"))],
        Disks = [Disk(0, "AMD-RAID Array", 2_000_000_000_000, BusType.RAID, DiskMediaType.Hdd, system: true)],
    };

    private static TargetPcInfo WindowsPeSession() => ModernLaptop() with
    {
        RunningSystem = new RunningSystemInfo
        {
            ProductName = "Windows 10 Pro",
            BuildNumber = 26100,
            Architecture = CpuArchitecture.X64,
            IsServer = false,
            IsWinPe = true,
            UiLanguage = "en-US",
            TimeZoneId = "Pacific Standard Time",
        },
        OemLicense = Key("Core"),
        IsElevated = true,
        Tpm = new TpmInfo { Present = true, SpecVersion = "2.0", IsEnabled = true },
    };

    private static TargetPcInfo LargeDiskOnBios() => CoffeeLakeDesktopInCsmMode() with
    {
        Disks = [Disk(0, "ST4000DM004", 4_000_787_030_016, BusType.Sata, DiskMediaType.Hdd, system: true, style: DiskPartitionStyle.Mbr)],
    };

    private static TargetPcInfo BorderlineMemoryAndDisk() => ModernLaptop() with
    {
        Memory = new MemoryInfo { InstalledBytes = null, UsableBytes = (long)(3.8 * GiB) },
        Disks = [Disk(0, "64 GB eMMC", 64_000_000_000, BusType.Mmc, DiskMediaType.Ssd, system: true)],
    };

    public static CpuInfo X64(string vendor, string name, int physical, int logical, int mhz, CpuFeatures features = ModernX86, string? hypervisor = null) => new()
    {
        Vendor = vendor,
        Name = name,
        Architecture = CpuArchitecture.X64,
        Is64BitCapable = true,
        PhysicalCores = physical,
        LogicalProcessors = logical,
        MaxClockMhz = mhz,
        Features = features,
        HypervisorPresent = hypervisor is not null,
        HypervisorVendor = hypervisor,
    };

    public static DiskInfo Disk(int number, string name, long bytes, BusType bus, DiskMediaType media, bool system = false, DiskPartitionStyle style = DiskPartitionStyle.Gpt) => new()
    {
        Number = number,
        Name = name,
        SizeBytes = bytes,
        Bus = bus,
        MediaType = media,
        PartitionStyle = style,
        IsSystemDisk = system,
    };

    public static HardwareDevice Pci(string name, string vendor, string device, string classCode, string? service, string? className, int? problem = 0, DriverPackageInfo? driver = null) => new()
    {
        InstanceId = $@"PCI\VEN_{vendor}&DEV_{device}&SUBSYS_0A5A1028&REV_01\3&11583659&0&E0",
        Name = name,
        ClassName = className,
        HardwareIds = [$@"PCI\VEN_{vendor}&DEV_{device}&SUBSYS_0A5A1028&REV_01", $@"PCI\VEN_{vendor}&DEV_{device}"],
        CompatibleIds = [$@"PCI\VEN_{vendor}&CC_{classCode}", $@"PCI\VEN_{vendor}&CC_{classCode[..4]}", $@"PCI\CC_{classCode}", $@"PCI\CC_{classCode[..4]}"],
        Service = service,
        ProblemCode = problem,
        Driver = driver,
    };

    public static DriverPackageInfo Vendor(string inf, string provider, string version) => new() { InfName = inf, Provider = provider, Version = version };

    public static HardwareDevice StandardNvme() =>
        Pci("Standard NVM Express Controller", "144D", "A808", "010802", "stornvme", "SCSIAdapter", driver: new DriverPackageInfo { InfName = "stornvme.inf", Provider = "Microsoft" });

    public static HardwareDevice IntelWifi(string device) =>
        Pci("Intel(R) Wi-Fi 6E AX211 160MHz", "8086", device, "028000", "Netwtw14", "Net", driver: new DriverPackageInfo { InfName = "netwtw14.inf", Provider = "Intel" });

    public static OemLicenseInfo Key(string editionId) => new()
    {
        HasFirmwareKey = true,
        MaskedKey = "XXXXX-XXXXX-XXXXX-XXXXX-6789N",
        EditionDescription = $"[4.0] {editionId} OEM:DM",
        EditionId = editionId,
        Channel = "OEM",
        TableOemId = "DELL",
    };

    public static RunningSystemInfo Windows11(string editionId, string language = "de-DE") => new()
    {
        ProductName = "Windows 10 Pro",
        EditionId = editionId,
        BuildNumber = 26200,
        DisplayVersion = "25H2",
        Architecture = CpuArchitecture.X64,
        IsServer = false,
        IsWinPe = false,
        UiLanguage = language,
        TimeZoneId = "W. Europe Standard Time",
        KeyboardLayouts = ["0407:00000407"],
    };

    public static RunningSystemInfo Windows10(string editionId, string language = "de-DE") => new()
    {
        ProductName = "Windows 10 Pro",
        EditionId = editionId,
        BuildNumber = 19045,
        DisplayVersion = "22H2",
        Architecture = CpuArchitecture.X64,
        IsServer = false,
        IsWinPe = false,
        UiLanguage = language,
        TimeZoneId = "W. Europe Standard Time",
        KeyboardLayouts = ["0407:00000407"],
    };
}
