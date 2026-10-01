// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Images;

/// <summary>Reads the metadata of the WIM files of a Windows setup or PE medium straight out of the image.</summary>
internal static class WindowsAnalyzer
{
    // Multi-architecture media keep one "sources" folder per architecture (x86\sources, x64\sources).
    private static readonly string[] WimGlobs =
    [
        "sources/install.wim", "sources/install.esd", "sources/install.swm", "sources/boot.wim",
        "*/sources/install.wim", "*/sources/install.esd", "*/sources/install.swm", "*/sources/boot.wim",
    ];

    public static WindowsImageInfo? Analyze(ImageFileSystem fileSystem, IReadOnlyList<ImageFileIndex> allTrees)
    {
        var index = fileSystem.Index;
        var paths = WimGlobs.SelectMany(index.FindFiles).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var needsLoader = index.HasFile("bootmgr.efi") && !BootFileProbe.HasEfiBootFiles(allTrees);
        if (paths.Count == 0)
        {
            return needsLoader ? new WindowsImageInfo { NeedsEfiLoaderExtraction = true } : null;
        }

        var images = new List<WindowsImageFile>();
        var unreadable = new List<string>();
        foreach (var path in paths)
        {
            using var stream = fileSystem.OpenFile(path);
            if (stream is null)
            {
                unreadable.Add(path);
                continue;
            }

            try
            {
                images.Add(new WindowsImageFile(path, stream.Length, WimMetadata.Read(stream)));
            }
            catch (Exception ex) when (ex is BootrixException or IOException or InvalidDataException or EndOfStreamException)
            {
                unreadable.Add(path);
            }
        }

        return new WindowsImageInfo { Images = images, UnreadableImages = unreadable, NeedsEfiLoaderExtraction = needsLoader };
    }

    /// <summary>The architecture all install images agree on, falling back to the boot image; unknown when they differ.</summary>
    public static WindowsArch Architecture(WindowsImageInfo info)
    {
        var architectures = info.InstallImages.Select(image => image.Metadata.Arch).Where(arch => arch != WindowsArch.Unknown).Distinct().ToList();
        if (architectures.Count == 0)
        {
            architectures = [.. info.Images.Select(image => image.Metadata.Arch).Where(arch => arch != WindowsArch.Unknown).Distinct()];
        }

        return architectures.Count == 1 ? architectures[0] : WindowsArch.Unknown;
    }

    public static int Build(WindowsImageInfo info)
    {
        var builds = info.InstallImages.Select(image => image.Metadata.Build).Where(build => build > 0).ToList();
        if (builds.Count == 0)
        {
            builds = [.. info.Images.Select(image => image.Metadata.Build).Where(build => build > 0)];
        }

        return builds.Count == 0 ? 0 : builds.Max();
    }
}
