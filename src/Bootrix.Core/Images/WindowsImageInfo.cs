// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Images;

/// <summary>A WIM, ESD or SWM found inside an image together with what its XML data says.</summary>
public sealed record WindowsImageFile(string Path, long Length, WimMetadata Metadata)
{
    public bool IsBootImage => Metadata.IsBootImage || Path.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Windows-specific findings of an inspection.</summary>
public sealed record WindowsImageInfo
{
    public IReadOnlyList<WindowsImageFile> Images { get; init; } = [];

    /// <summary>WIM files that were found but whose metadata could not be read (path).</summary>
    public IReadOnlyList<string> UnreadableImages { get; init; } = [];

    /// <summary>The ISO has bootmgr.efi but no EFI\BOOT loader (Vista SP1, Windows 7): the loader has to be taken from install.wim.</summary>
    public bool NeedsEfiLoaderExtraction { get; init; }

    public IEnumerable<WindowsImageFile> InstallImages => Images.Where(image => !image.IsBootImage);

    public bool HasInstallImage => InstallImages.Any();

    public bool HasBootImage => Images.Any(image => image.IsBootImage);

    /// <summary>The editions of the install image(s); empty for a PE-only medium.</summary>
    public IEnumerable<Wim.WimEdition> Editions => InstallImages.SelectMany(image => image.Metadata.Editions);
}
