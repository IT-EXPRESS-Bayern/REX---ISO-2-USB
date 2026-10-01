// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Images.Families;
using Bootrix.Core.Images.Integrity;
using Bootrix.Core.Images.Iso;
using DiscUtils.Streams;

namespace Bootrix.Core.Images;

/// <summary>
/// The inspection proper: works on one seekable stream that holds the image, or the start of it when a compressed
/// image was decoded only partially, and returns everything that can be learnt without writing anything.
/// </summary>
internal static class ImageAnalyzer
{
    private const int EntryLimit = 300_000;
    private const int MaxBootImages = 4;
    private const int ListedBootImageFiles = 64;
    private const int ListedLargestFiles = 8;
    private const int ListedRootEntries = 64;

    /// <summary>Everything the individual probes found; <see cref="ImageAnalyzer.Assemble"/> turns it into the result.</summary>
    private sealed class Evidence
    {
        public required Stream Image { get; init; }

        public string? FileName { get; init; }

        public long? ImageLength { get; init; }

        public bool Partial { get; init; }

        public ImageContainer Container { get; set; }

        public bool IsOptical => Container is ImageContainer.Iso9660 or ImageContainer.IsoUdfBridge or ImageContainer.Udf;

        public DiskLayout Layout { get; set; } = new();

        public Iso9660Volume? Iso { get; set; }

        public ElToritoCatalog? Catalog { get; set; }

        /// <summary>The file system whose tree describes the image: the ISO's, or the first FAT volume of a disk image.</summary>
        public ImageFileSystem? Primary { get; set; }

        public List<ImageFileSystem> Volumes { get; } = [];

        public List<ImageFileSystem> BootVolumes { get; } = [];

        public List<ImageWarning> Warnings { get; } = [];

        public WindowsImageInfo? Windows { get; set; }

        public IEnumerable<ImageFileSystem> AllVolumes => Volumes.Concat(BootVolumes);

        public List<ImageFileIndex> Trees => [.. AllVolumes.Select(volume => volume.Index)];

        public string? ReadText(string path) => AllVolumes.Select(volume => volume.ReadText(path)).FirstOrDefault(text => text is not null);
    }

    public static ImageInspection Analyze(Stream image, string? fileName, long? imageLength, bool partial, CancellationToken cancellationToken)
    {
        var evidence = new Evidence { Image = image, FileName = fileName, ImageLength = imageLength, Partial = partial };
        try
        {
            evidence.Container = ImageContainerSniffer.Detect(image);
            evidence.Layout = DiskLayoutReader.Read(image);
            Probe(evidence, cancellationToken);
            return Assemble(evidence);
        }
        finally
        {
            foreach (var volume in evidence.AllVolumes)
            {
                volume.Dispose();
            }
        }
    }

    private static void Probe(Evidence e, CancellationToken cancellationToken)
    {
        var image = e.Image;
        switch (e.Container)
        {
            case ImageContainer.Iso9660 or ImageContainer.IsoUdfBridge or ImageContainer.Udf:
                e.Iso = Iso9660Reader.Read(image);
                e.Catalog = e.Iso?.BootCatalogSector is { } sector ? ElToritoParser.ReadCatalog(image, sector) : null;
                if (!e.Partial)
                {
                    OpenIso(e, cancellationToken);
                }

                break;
            case ImageContainer.RawDisk:
                e.Volumes.AddRange(PartitionFileSystems.Open(image, e.Layout, EntryLimit, cancellationToken));
                break;
            case ImageContainer.Vhd when ImageContainerSniffer.IsFixedVhd(ImageContainerSniffer.ReadAt(image, image.Length - 512, 512)):
                // A fixed VHD is the raw disk followed by a 512-byte footer.
                var disk = new SubStream(image, Ownership.None, 0, image.Length - 512);
                e.Layout = DiskLayoutReader.Read(disk);
                e.Volumes.AddRange(PartitionFileSystems.Open(disk, e.Layout, EntryLimit, cancellationToken));
                break;
            case ImageContainer.FatVolume:
                if (ImageFileSystem.OpenFatRegion(image, 0, image.Length, EntryLimit, cancellationToken) is { } floppy)
                {
                    e.Volumes.Add(floppy);
                }

                break;
        }

        e.Primary ??= e.Volumes.FirstOrDefault();
        e.Windows = AnalyzeWindows(e);
    }

    private static void OpenIso(Evidence e, CancellationToken cancellationToken)
    {
        var fileSystem = ImageFileSystem.OpenIso(e.Image, e.Iso?.HasJoliet ?? false, e.Container != ImageContainer.Iso9660, EntryLimit, cancellationToken);
        if (fileSystem is null)
        {
            e.Warnings.Add(new ImageWarning(ImageWarningKeys.FileSystemUnreadable));
            return;
        }

        e.Volumes.Add(fileSystem);
        e.Primary = fileSystem;

        var candidates = e.Catalog?.Entries
            .Where(entry => entry.Bootable && (entry.IsEfi || entry.Emulation != ElToritoEmulation.None))
            .DistinctBy(entry => entry.ImageSector)
            .Take(MaxBootImages) ?? [];

        foreach (var entry in candidates)
        {
            if (BootImageReader.Open(e.Image, entry, EntryLimit, cancellationToken) is { } volume)
            {
                e.BootVolumes.Add(volume);
            }
            else if (entry.IsEfi)
            {
                e.Warnings.Add(new ImageWarning(ImageWarningKeys.BootImageUnreadable, WarningSeverity.Info));
            }
        }
    }

    private static WindowsImageInfo? AnalyzeWindows(Evidence e)
    {
        var trees = e.Trees;
        foreach (var volume in e.Volumes)
        {
            var info = WindowsAnalyzer.Analyze(volume, trees);
            if (info is null)
            {
                continue;
            }

            e.Warnings.AddRange(info.UnreadableImages.Select(path => new ImageWarning(ImageWarningKeys.WimUnreadable, WarningSeverity.Warning, path)));
            if (info.NeedsEfiLoaderExtraction)
            {
                e.Warnings.Add(new ImageWarning(ImageWarningKeys.Win7EfiLoaderMissing));
            }

            return info;
        }

        return null;
    }

    private static ImageInspection Assemble(Evidence e)
    {
        var trees = e.Trees;
        var label = e.Iso?.VolumeId ?? e.Primary?.VolumeLabel;
        var match = FamilyMatcher.Default.Match(new FamilyContext
        {
            IsOptical = e.IsOptical,
            Label = label,
            FileName = e.FileName,
            ImageBytes = e.ImageLength ?? e.Image.Length,
            Trees = trees,
            ReadText = e.ReadText,
            Layout = e.Layout,
        });

        var layout = e.Layout;
        var hybrid = e.IsOptical
            ? layout.HasMbrSignature && !layout.IsVolumeImage && (layout.MbrPartitions.Any(p => !p.IsProtective) || layout.HasGpt)
            : e.Container is ImageContainer.RawDisk or ImageContainer.Vhd && layout.HasPartitionTable;

        var efiArchitectures = BootFileProbe.EfiArchitectures(trees);
        var hasEfiFiles = BootFileProbe.HasEfiBootFiles(trees) && e.Windows?.NeedsEfiLoaderExtraction != true;
        var hasBiosFiles = BootFileProbe.HasBiosBootFiles(trees);
        var kind = DetermineKind(e, match, hybrid, hasBiosFiles || hasEfiFiles || e.Catalog is not null);

        var arch = e.Windows is null ? WindowsArch.Unknown : WindowsAnalyzer.Architecture(e.Windows);
        if (arch == WindowsArch.Unknown && kind is ImageKind.WindowsSetup or ImageKind.WindowsPe && efiArchitectures.Count == 1)
        {
            arch = efiArchitectures[0];
        }

        AddFindings(e, kind, match, hybrid, hasBiosFiles, hasEfiFiles);

        var index = e.IsOptical ? e.Primary?.Index : null;
        var length = e.ImageLength ?? (e.Partial ? 0 : e.Image.Length);
        var profile = new ImageProfile
        {
            Kind = kind,
            Family = match?.Family,
            VolumeLabel = label,
            TotalBytes = index?.TotalBytes ?? length,
            LargestFileBytes = index?.LargestFileBytes ?? 0,
            IsHybrid = hybrid,
            HasBiosBootFiles = hasBiosFiles,
            HasEfiBootFiles = hasEfiFiles,
            HasElToritoBios = e.Catalog?.HasBios ?? false,
            HasElToritoEfi = e.Catalog?.HasEfi ?? false,
            HasEmulatedBootImage = e.Catalog?.Entries.Any(entry => entry.Bootable && entry.Emulation != ElToritoEmulation.None) ?? false,
            Arch = arch,
            WindowsBuild = e.Windows is null ? 0 : WindowsAnalyzer.Build(e.Windows),
            Container = e.Container,
            HasEspPartition = layout.HasEfiSystemPartition,
            HasGpt = layout.HasGpt,
            HasProtectiveMbr = layout.HasProtectiveMbr && layout.MbrPartitions.All(partition => partition.IsProtective),
            ImageBytes = length,
        };

        var tree = e.Primary?.Index;
        return new ImageInspection
        {
            Profile = profile,
            Container = e.Container,
            FileName = e.FileName,
            FileLength = e.Image.Length,
            ImageLength = e.ImageLength,
            IsPartial = e.Partial,
            ReleaseInfo = ReleaseInfoReader.Read(e.ReadText),
            Volume = e.Iso,
            BootCatalog = e.Catalog,
            BootImageFiles = e.BootVolumes.Count == 0
                ? []
                : [.. e.BootVolumes[0].Index.Files.Select(file => file.Path).Order(StringComparer.OrdinalIgnoreCase).Take(ListedBootImageFiles)],
            Layout = layout,
            Windows = e.Windows,
            EfiArchitectures = efiArchitectures,
            FileCount = tree?.FileCount ?? 0,
            RootEntries = tree is null
                ? []
                : [.. tree.RootEntries().Take(ListedRootEntries).Select(entry => new ImageFileEntry(entry.Name, tree.LengthOf(entry.Name) ?? 0, entry.IsDirectory))],
            LargestFiles = tree is null
                ? []
                : [.. tree.Files.OrderByDescending(file => file.Length).Take(ListedLargestFiles).Select(file => new ImageFileEntry(file.Path, file.Length))],
            Warnings = e.Warnings,
            IsTruncated = e.Warnings.Any(warning => warning.Severity == WarningSeverity.Error),
        };
    }

    private static ImageKind DetermineKind(Evidence e, FamilyMatch? match, bool hybrid, bool hasBootData)
    {
        if (match is not null)
        {
            if (match.Kind.Equals("Linux", StringComparison.OrdinalIgnoreCase))
            {
                return e.IsOptical ? (hybrid ? ImageKind.LinuxHybrid : ImageKind.LinuxIsoOnly) : ImageKind.RawDisk;
            }

            if (Enum.TryParse<ImageKind>(match.Kind, ignoreCase: true, out var kind))
            {
                return kind;
            }
        }

        return e.Container switch
        {
            ImageContainer.Wim => e.Windows is { HasInstallImage: false, HasBootImage: true } ? ImageKind.WindowsPe : ImageKind.WindowsSetup,
            ImageContainer.AppleImage => ImageKind.Apple,
            ImageContainer.RawDisk or ImageContainer.Vhd or ImageContainer.Vhdx => e.Layout.HasApm ? ImageKind.Apple : ImageKind.RawDisk,
            _ when e.IsOptical && hasBootData => hybrid ? ImageKind.LinuxHybrid : ImageKind.LinuxIsoOnly,
            _ => ImageKind.Data,
        };
    }

    private static void AddFindings(Evidence e, ImageKind kind, FamilyMatch? match, bool hybrid, bool hasBiosFiles, bool hasEfiFiles)
    {
        if (e.Volumes.Any(volume => volume.Index.Incomplete))
        {
            e.Warnings.Add(new ImageWarning(ImageWarningKeys.FileTreeIncomplete));
        }

        if (e.Layout.HasGpt && e.Layout.GptSectorSize == 4096)
        {
            e.Warnings.Add(new ImageWarning(ImageWarningKeys.Sector4kImage));
        }

        var bootable = hasBiosFiles || hasEfiFiles || e.Catalog is not null || e.Layout.HasBootCode || e.Layout.HasEfiSystemPartition || hybrid;
        if (match is null && !bootable && !e.Partial && kind is ImageKind.Data or ImageKind.LinuxIsoOnly or ImageKind.RawDisk)
        {
            e.Warnings.Add(new ImageWarning(ImageWarningKeys.NotBootable));
        }

        e.Warnings.AddRange(ImageIntegrityChecker.CheckImage(e.Image, e.ImageLength ?? (e.Partial ? null : e.Image.Length), e.Partial ? null : e.FileName));
    }
}
