// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Planning;

/// <summary>The sizes and barriers the planner works with.</summary>
internal static class PlanLimits
{
    public const long Kib = 1024;
    public const long Mib = 1024 * Kib;
    public const long Gib = 1024 * Mib;
    public const long Tib = 1024 * Gib;

    /// <summary>Microsoft's minimum for an ESP on 4Kn drives and the usual size elsewhere; 100 MB is the 512-byte minimum.</summary>
    public const long EspBytes = 300 * Mib;

    /// <summary>Current Microsoft guidance for the MSR on Windows 10 and later.</summary>
    public const long MsrBytes = 16 * Mib;

    public const long UefiNtfsBytes = Mib;

    public const long BiosBootBytes = Mib;

    public const long MinPersistenceBytes = 16 * Mib;

    public const long WindowsToGoMinBytes = 16 * Gib;

    /// <summary>1024 cylinders of 255 heads and 63 sectors: what a CHS-only BIOS can read, about 7.84 GiB.</summary>
    public const long ChsLimitBytes = 1024L * 255 * 63 * 512;

    /// <summary>28-bit LBA: 128 GiB, the limit of old IDE BIOSes and some bridges.</summary>
    public const long Lba28LimitBytes = (1L << 28) * 512;

    /// <summary>MS-DOS cannot use FAT16 beyond 2 GiB (32 KiB clusters).</summary>
    public const long DosFat16MaxBytes = 2 * Gib;

    public const long Fat12MaxBytes = 255 * Mib;

    /// <summary>FAT16 with 64 KiB clusters; Windows NT only.</summary>
    public const long Fat16MaxBytes = 4000 * Mib;

    public const long FileSizeLimitFat = 4 * Gib - 1;

    /// <summary>FAT32 is the choice up to this size for data media, because Windows itself will not format more.</summary>
    public const long Fat32DataLimitBytes = 32 * Gib;

    /// <summary>File system slack, boot files and partition gaps on top of the image's own size.</summary>
    public static long ExtractOverhead(long imageBytes) => Math.Max(32 * Mib, imageBytes / 50);
}
