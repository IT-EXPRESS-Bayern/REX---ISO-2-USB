// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images;

/// <summary>
/// File name patterns for image pickers and drop targets. The list is Rufus' filter plus the extensions
/// that matter for the extra formats (.bin for ChromeOS Flex and similar raw dumps, .raw, .dmg, .ffu, .swm).
/// Detection never trusts the extension; it only decides what is offered to the user.
/// </summary>
public static class ImageFileTypes
{
    private static readonly string[] DiskImages =
        [".iso", ".img", ".bin", ".raw", ".wic", ".usb", ".vhd", ".vhdx", ".dmg", ".ffu", ".vtsi"];

    private static readonly string[] WindowsImages = [".wim", ".esd", ".swm"];

    private static readonly string[] Compressed = [".gz", ".bz2", ".bzip2", ".xz", ".zst", ".zstd", ".lzma", ".z", ".zip"];

    public static IReadOnlyList<string> DiskImageExtensions => DiskImages;

    public static IReadOnlyList<string> WindowsImageExtensions => WindowsImages;

    public static IReadOnlyList<string> CompressedExtensions => Compressed;

    /// <summary>Semicolon separated patterns such as <c>*.iso;*.img</c> for a file dialog.</summary>
    public static string FilterPattern { get; } =
        string.Join(';', DiskImages.Concat(WindowsImages).Concat(Compressed).Select(extension => "*" + extension));

    public static bool IsKnown(string path) =>
        IsCompressed(path) || Matches(Path.GetExtension(path), DiskImages) || Matches(Path.GetExtension(path), WindowsImages);

    public static bool IsCompressed(string path) => Matches(Path.GetExtension(path), Compressed);

    /// <summary>
    /// Extension of the payload: for <c>disk.img.xz</c> this is <c>.img</c>, otherwise the last extension.
    /// </summary>
    public static string GetImageExtension(string path)
    {
        var name = Path.GetFileName(path);
        if (IsCompressed(name))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        return Path.GetExtension(name);
    }

    private static bool Matches(string extension, string[] list) =>
        list.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
