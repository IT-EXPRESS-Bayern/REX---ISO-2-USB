// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Optical;

public sealed record FolderScan(
    long TotalBytes,
    long PayloadSectors,
    int FileCount,
    int DirectoryCount,
    int MaxDepth,
    int LongestNameLength,
    string? LargestFile,
    long LargestFileBytes,
    bool LooksLikeWindowsSetup);

public enum FolderBurnWarning
{
    /// <summary>The tree is deeper than the eight levels ISO 9660 allows; that layer is left out.</summary>
    Iso9660Dropped,

    /// <summary>A name is longer than Joliet's 64 characters; that layer is left out.</summary>
    JolietDropped,

    /// <summary>A file of 4 GB or more is on the disc, which only UDF can hold.</summary>
    UdfOnlyForLargeFile,
}

public sealed record FolderBurnPlan(
    DiscFileSystems FileSystems,
    UdfRevision UdfRevision,
    string VolumeLabel,
    FolderScan Scan,
    IReadOnlyList<FolderBurnWarning> Warnings);

/// <summary>
/// Looks at a folder before it is turned into a disc and picks file systems IMAPI2FS can really produce
/// from it. IMAPI fails late and with codes like IMAPI_E_DATA_TOO_BIG; deciding here gives a clear message
/// before anything is staged or written.
/// </summary>
public static class FolderBurnPlanner
{
    /// <summary>ISO 9660 level 2 allows eight directory levels below the root.</summary>
    public const int Iso9660MaxDepth = 8;

    public const int JolietMaxNameLength = 64;

    public static FolderScan Scan(string folder, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            throw new BootrixException(ErrorCode.ImageUnreadable, folder) { Arguments = [$"folder '{folder}' does not exist"] };
        }

        long total = 0;
        long payload = 0;
        long largestBytes = 0;
        string? largest = null;
        int files = 0, directories = 0, depth = 0, longest = 0;

        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in item.Directory.EnumerateFileSystemInfos())
            {
                longest = Math.Max(longest, entry.Name.Length);
                if (entry is DirectoryInfo directory)
                {
                    directories++;
                    depth = Math.Max(depth, item.Depth + 1);
                    pending.Push((directory, item.Depth + 1));
                }
                else if (entry is FileInfo file)
                {
                    files++;
                    total += file.Length;
                    payload += SectorMath.SectorsFor(file.Length);
                    if (file.Length > largestBytes)
                    {
                        largestBytes = file.Length;
                        largest = file.FullName;
                    }
                }
            }
        }

        return new FolderScan(total, payload, files, directories, depth, longest, largest, largestBytes, LooksLikeWindowsSetup(root));
    }

    public static FolderBurnPlan Plan(FolderScan scan, FolderBurnRequest request, OpticalMedia media, string? volumeLabel)
    {
        var warnings = new List<FolderBurnWarning>();
        var systems = request.FileSystems ?? DefaultFileSystems(media.Family);
        var udfRevision = request.FileSystems is null && media.Family == OpticalMediaFamily.BluRay ? UdfRevision.Udf250 : request.UdfRevision;

        if (scan.LargestFileBytes > SectorMath.Iso9660MaxFileSize)
        {
            // IMAPI2FS cannot reproduce what a Windows installation disc needs here (UDF 1.02 with an ISO 9660 stub, files in boot order),
            // so the remaster path is the answer; for ordinary data a UDF-only disc is fine.
            if (scan.LooksLikeWindowsSetup)
            {
                throw new BootrixException(ErrorCode.DiscFileTooLarge, scan.LargestFile) { Arguments = [Path.GetFileName(scan.LargestFile)] };
            }

            if (systems.HasFlag(DiscFileSystems.Udf))
            {
                systems = DiscFileSystems.Udf;
                warnings.Add(FolderBurnWarning.UdfOnlyForLargeFile);
            }
            else
            {
                throw new BootrixException(ErrorCode.DiscFileTooLarge, scan.LargestFile) { Arguments = [Path.GetFileName(scan.LargestFile)] };
            }
        }

        if (scan.MaxDepth > Iso9660MaxDepth && systems.HasFlag(DiscFileSystems.Iso9660) && systems != DiscFileSystems.Iso9660)
        {
            systems &= ~DiscFileSystems.Iso9660;
            warnings.Add(FolderBurnWarning.Iso9660Dropped);
        }

        if (scan.LongestNameLength > JolietMaxNameLength && systems.HasFlag(DiscFileSystems.Joliet) && systems != DiscFileSystems.Joliet)
        {
            systems &= ~DiscFileSystems.Joliet;
            warnings.Add(FolderBurnWarning.JolietDropped);
        }

        if (media.IsPresent && scan.PayloadSectors > media.FreeSectors)
        {
            throw new BootrixException(ErrorCode.DeviceTooSmall, "folder does not fit on the disc")
            {
                Arguments = [SectorMath.FormatBytes(SectorMath.ToBytes(scan.PayloadSectors)), SectorMath.FormatBytes(media.FreeBytes)],
            };
        }

        return new FolderBurnPlan(systems, udfRevision, NormalizeLabel(volumeLabel, systems, Path.GetFileName(request.SourceFolder.TrimEnd('\\', '/'))), scan, warnings);
    }

    /// <summary>
    /// Cleans a volume label so IMAPI accepts it: 16 characters if Joliet is on the disc, 32 otherwise, and
    /// letters, digits, space, dash, underscore and dot only.
    /// </summary>
    public static string NormalizeLabel(string? label, DiscFileSystems systems, string fallback)
    {
        var maxLength = systems.HasFlag(DiscFileSystems.Joliet) ? 16 : 32;
        var cleaned = new StringBuilder();
        foreach (var c in (string.IsNullOrWhiteSpace(label) ? fallback : label).Trim())
        {
            cleaned.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_');
        }

        var text = cleaned.ToString().Trim();
        if (text.Length == 0)
        {
            text = "DISC";
        }

        return text.Length > maxLength ? text[..maxLength].TrimEnd() : text;
    }

    public static DiscFileSystems DefaultFileSystems(OpticalMediaFamily family) =>
        family == OpticalMediaFamily.BluRay ? DiscFileSystems.Udf : DiscFileSystems.Iso9660 | DiscFileSystems.Joliet | DiscFileSystems.Udf;

    private static bool LooksLikeWindowsSetup(DirectoryInfo root)
    {
        var sources = Path.Combine(root.FullName, "sources");
        var hasInstallImage = File.Exists(Path.Combine(sources, "install.wim")) || File.Exists(Path.Combine(sources, "install.esd")) || File.Exists(Path.Combine(sources, "install.swm"));
        var bootable = File.Exists(Path.Combine(root.FullName, "bootmgr")) || Directory.Exists(Path.Combine(root.FullName, "efi"));
        return hasInstallImage && bootable;
    }
}
