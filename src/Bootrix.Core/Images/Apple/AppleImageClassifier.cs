// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// Decides what kind of Apple image a file or decoded volume is and what the user should be told about it:
/// whether it is Mac-only, whether it starts a Mac and what limits apply to media made on Windows.
/// </summary>
public static class AppleImageClassifier
{
    /// <summary>Opens the image (DMG, sparse image, sparse bundle or raw file), classifies the volume inside and closes it again.</summary>
    public static AppleImageInfo Classify(string path, DmgReaderOptions? options = null, ILogger? logger = null)
    {
        using var opened = AppleImageOpener.Open(path, options, logger);
        return Classify(opened.Volume, opened.Container, opened.Dmg);
    }

    /// <summary>Classifies an already decoded volume; the stream must be seekable.</summary>
    public static AppleImageInfo Classify(Stream volume) => Classify(volume, AppleImageContainer.Raw, null);

    internal static AppleImageInfo Classify(Stream volume, AppleImageContainer container, DmgInfo? dmg)
    {
        var scan = AppleImageScan.Run(volume);
        var kind = DetermineKind(scan);
        var macOnly = IsMacOnly(scan, kind);
        var bootable = IsBootable(scan);

        return new AppleImageInfo
        {
            Container = container,
            Kind = kind,
            Scheme = scan.Apm is not null ? AppleImageScheme.Apm
                : scan.Gpt is not null ? AppleImageScheme.Gpt
                : scan.Mbr.Count > 0 ? AppleImageScheme.Mbr
                : AppleImageScheme.None,
            FileSystem = scan.BareVolume.Found
                ? scan.BareVolume.FileSystem
                : scan.Partitions.FirstOrDefault(p => p.FileSystem != AppleFileSystem.Unknown)?.FileSystem ?? AppleFileSystem.Unknown,
            VolumeSize = scan.Length,
            IsBootable = bootable,
            IsMacOnly = macOnly,
            HasIso9660 = scan.HasIso9660,
            Partitions = scan.Partitions,
            Hints = BuildHints(scan, kind, macOnly, bootable),
            Dmg = dmg,
        };
    }

    private static AppleImageKind DetermineKind(AppleImageScan scan)
    {
        var hasMacVolume = scan.BareVolume.Found || scan.Partitions.Any(p => p.FileSystem != AppleFileSystem.Unknown);

        // "ER" in block 0 is not enough: PC ISOs built with xorriso carry one too, together with an MBR.
        if (scan.HasIso9660)
        {
            if (scan.Apm is not null)
            {
                return scan.HasMbrSignature ? AppleImageKind.IsoHybridWithApm : AppleImageKind.HybridDisc;
            }

            return scan.HasApplePartitionTypes || hasMacVolume ? AppleImageKind.HybridDisc : AppleImageKind.NotApple;
        }

        if (scan.Apm is not null)
        {
            return AppleImageKind.ApplePartitionMap;
        }

        if (scan.HasApplePartitionTypes)
        {
            return AppleImageKind.GptMacDisk;
        }

        if (scan.HasMbrApplePartition)
        {
            return AppleImageKind.MbrMacDisk;
        }

        return scan.BareVolume.FileSystem switch
        {
            AppleFileSystem.Hfs => AppleImageKind.HfsVolume,
            AppleFileSystem.HfsPlus or AppleFileSystem.HfsX => AppleImageKind.HfsPlusVolume,
            AppleFileSystem.Apfs => AppleImageKind.ApfsContainer,
            _ => AppleImageKind.NotApple,
        };
    }

    private static bool IsMacOnly(AppleImageScan scan, AppleImageKind kind)
    {
        if (scan.HasIso9660 || scan.HasMbrBootCode)
        {
            return false;
        }

        switch (kind)
        {
            case AppleImageKind.HfsVolume:
            case AppleImageKind.HfsPlusVolume:
            case AppleImageKind.ApfsContainer:
                return true;
            case AppleImageKind.ApplePartitionMap:
                var data = scan.Partitions.Where(p => !ApmPartitionTypes.IsStructural(p.Type)).ToList();
                return data.Count > 0 && data.All(p => ApmPartitionTypes.IsApple(p.Type));
            case AppleImageKind.GptMacDisk:
                return scan.Gpt!.Entries.All(e => AppleGptTypes.IsApple(e.Type) || e.Type == AppleGptTypes.EfiSystem);
            case AppleImageKind.MbrMacDisk:
                return scan.Mbr.All(e => e.Type is 0xAF or 0xAB or 0xEE);
            default:
                return false;
        }
    }

    private static bool IsBootable(AppleImageScan scan)
    {
        var blessed = scan.BareVolume.BlessedFolder != 0 || scan.Partitions.Any(p => p.IsBlessed);
        var bootSupport = scan.Gpt?.Entries.Any(e => AppleGptTypes.IsBootSupport(e.Type)) == true;
        return blessed || bootSupport || (scan.HasIso9660 && scan.HasElTorito);
    }

    private static List<AppleImageHint> BuildHints(AppleImageScan scan, AppleImageKind kind, bool macOnly, bool bootable)
    {
        var hints = new List<AppleImageHint>();
        var fileSystems = scan.Partitions.Select(p => p.FileSystem).Append(scan.BareVolume.FileSystem).ToList();
        var blessed = scan.BareVolume.BlessedFolder != 0 || scan.Partitions.Any(p => p.IsBlessed);
        var hasApfs = fileSystems.Contains(AppleFileSystem.Apfs);
        var hasHfsPlus = fileSystems.Contains(AppleFileSystem.HfsPlus) || fileSystems.Contains(AppleFileSystem.HfsX);

        if (macOnly)
        {
            hints.Add(AppleImageHint.MacOnly);
        }

        if (kind is AppleImageKind.HfsVolume or AppleImageKind.HfsPlusVolume or AppleImageKind.ApfsContainer)
        {
            hints.Add(AppleImageHint.BareVolume);
        }

        if (kind == AppleImageKind.HybridDisc)
        {
            hints.Add(AppleImageHint.HybridDisc);
        }

        if (kind == AppleImageKind.IsoHybridWithApm)
        {
            hints.Add(AppleImageHint.IsoHybridWithApm);
        }

        if (blessed)
        {
            hints.Add(AppleImageHint.BootableMacVolume);
        }
        else if (hasHfsPlus && !hasApfs && !bootable && kind != AppleImageKind.NotApple)
        {
            hints.Add(AppleImageHint.DataVolumeOnly);
        }

        if (fileSystems.Contains(AppleFileSystem.Hfs))
        {
            hints.Add(AppleImageHint.ClassicHfs);
        }

        if (hasApfs)
        {
            hints.Add(AppleImageHint.ApfsContainer);
        }

        // Only HFS+/APFS media are of any use to the Macs these notes are about.
        if (macOnly && bootable && (hasHfsPlus || hasApfs))
        {
            hints.Add(AppleImageHint.T2Restriction);
            hints.Add(AppleImageHint.AppleSiliconUnsupported);
        }

        if (scan.Partitions.Any(p => !IsFreeSpace(p) && p.Length > 0 && p.Offset + p.Length > scan.Length))
        {
            hints.Add(AppleImageHint.PartitionExceedsImage);
        }

        return hints;
    }

    private static bool IsFreeSpace(AppleImagePartition partition) =>
        partition.Type.Equals(ApmPartitionTypes.Free, StringComparison.OrdinalIgnoreCase)
        || partition.Type.Equals("Apple_Void", StringComparison.OrdinalIgnoreCase);
}
