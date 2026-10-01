// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Profiles;

/// <summary>
/// Everything needed to repeat a job except the target device. A spec is what profiles store,
/// what the CLI accepts and what the GUI builds from its form.
/// </summary>
public sealed record JobSpec
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Name { get; init; } = "";

    public JobKind Kind { get; init; } = JobKind.WriteImage;

    public ImageReference? Source { get; init; }

    public TargetOptions Target { get; init; } = new();

    public WindowsSetupOptions Windows { get; init; } = new();

    public VerifyOptions Verify { get; init; } = new();

    public ReportOptions Report { get; init; } = new();
}

/// <summary>
/// Points at an image either by file, by a catalog rule such as "windows11/25H2/de-DE/x64"
/// or by URL. A pinned SHA-256 makes the job reproducible.
/// </summary>
public sealed record ImageReference
{
    public string? Path { get; init; }

    public string? CatalogQuery { get; init; }

    public string? Url { get; init; }

    public string? Sha256 { get; init; }
}

public sealed record TargetOptions
{
    public PartitionScheme Scheme { get; init; } = PartitionScheme.Auto;

    public TargetFirmware Firmware { get; init; } = TargetFirmware.Auto;

    public FileSystemKind FileSystem { get; init; } = FileSystemKind.Auto;

    public WriteMode Mode { get; init; } = WriteMode.Auto;

    public string? Label { get; init; }

    public int? ClusterSizeBytes { get; init; }

    public bool QuickFormat { get; init; } = true;

    /// <summary>Work around old BIOSes: partition at a classic offset, CHS values and a bootable flag they accept.</summary>
    public bool LegacyBiosFixes { get; init; }

    public int PersistenceMegabytes { get; init; }

    public bool CheckBadBlocks { get; init; }

    /// <summary>Where the first partition starts when <see cref="LegacyBiosFixes"/> is on.</summary>
    public LegacyPartitionStart LegacyStart { get; init; } = LegacyPartitionStart.Kib64;

    /// <summary>Put the file system at LBA 0 with no partition table, for floppies and BIOSes that only offer diskette emulation.</summary>
    public bool Superfloppy { get; init; }

    /// <summary>Install Windows onto the device itself (Windows To Go) instead of creating setup media.</summary>
    public bool WindowsToGo { get; init; }
}

public sealed record WindowsSetupOptions
{
    public bool BypassTpm { get; init; }

    public bool BypassSecureBoot { get; init; }

    public bool BypassRam { get; init; }

    public bool BypassCpu { get; init; }

    public bool BypassStorage { get; init; }

    /// <summary>Create this local account during setup instead of requiring a Microsoft account.</summary>
    public string? LocalAccountName { get; init; }

    /// <summary>Pattern for the computer name, e.g. "PC-{serial}"; validated to 15 NetBIOS characters.</summary>
    public string? ComputerNamePattern { get; init; }

    public string? TimeZone { get; init; }

    public string? UiLanguage { get; init; }

    public bool SkipPrivacyQuestions { get; init; }

    public bool DisableBitLocker { get; init; }

    public string? Edition { get; init; }

    public BootCertificate BootCertificate { get; init; } = BootCertificate.Auto;

    public IReadOnlyList<string> DriverFolders { get; init; } = [];

    /// <summary>
    /// Also add the drivers to boot.wim (Setup) and install.wim with DISM. Off by default: the $WinPEDriver$ folder is
    /// loaded by Setup and carried into the installed system, and the DISM route rewrites gigabytes on the stick.
    /// </summary>
    public bool InjectDriversIntoImages { get; init; }

    public ExistingAnswerFilePolicy ExistingAnswerFile { get; init; } = ExistingAnswerFilePolicy.ReplaceAndKeepOriginal;
}

public sealed record VerifyOptions
{
    public bool ReadBack { get; init; } = true;

    /// <summary>Unplug and re-insert the stick before the final comparison to expose controllers that fake a flush.</summary>
    public bool Paranoid { get; init; }
}

public sealed record ReportOptions
{
    public bool CreateReport { get; init; }

    public string? CustomerReference { get; init; }

    public string? Technician { get; init; }
}
