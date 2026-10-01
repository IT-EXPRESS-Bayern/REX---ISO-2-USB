// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// What gets copied onto a Windows setup medium and in which order. A pure function of the file list, so
/// it can be tested without an image or a disk.
/// </summary>
/// <remarks>
/// The boot files come last: a copy that is interrupted must not leave a medium that starts the boot
/// manager and then fails halfway through setup. Without bootmgr or the EFI loader the firmware simply
/// does not offer the stick.
/// </remarks>
public sealed class WindowsCopyPlan
{
    private WindowsCopyPlan(IReadOnlyList<string> directories, IReadOnlyList<CopyItem> items, IReadOnlyList<MediaSourceFile> excluded, long splitPartBytes)
    {
        Directories = directories;
        Items = items;
        Excluded = excluded;
        SplitPartBytes = splitPartBytes;
        TotalBytes = items.Sum(item => item.Bytes);
    }

    /// <summary>Directories to create, parents before children.</summary>
    public IReadOnlyList<string> Directories { get; }

    public IReadOnlyList<CopyItem> Items { get; }

    public IReadOnlyList<MediaSourceFile> Excluded { get; }

    public long TotalBytes { get; }

    /// <summary>Size limit for the parts of a split install image.</summary>
    public long SplitPartBytes { get; }

    public bool HasSplit => Items.Any(item => item.Action == CopyAction.SplitInstallImage);

    /// <summary>
    /// Space the medium takes on a FAT volume with this cluster size: every file in whole clusters, a cluster per
    /// directory. A split install image counts a little more than the image, as every part has its own tables.
    /// </summary>
    public long BytesOnFat(int clusterBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clusterBytes, 512);
        long Rounded(long bytes) => (bytes + clusterBytes - 1) / clusterBytes * clusterBytes;

        long total = Directories.Count * (long)clusterBytes;
        foreach (var item in Items)
        {
            total += item.Action == CopyAction.SplitInstallImage
                ? Rounded(item.Bytes + item.Bytes / 100 + (4L << 20))
                : Rounded(item.Bytes);
        }

        return total;
    }

    public static WindowsCopyPlan Create(IWindowsMediaSource source, WindowsCopyOptions options) =>
        Create(source.Files, source.Directories, options);

    /// <exception cref="BootrixException">A file is larger than the target file system allows and cannot be split.</exception>
    public static WindowsCopyPlan Create(IEnumerable<MediaSourceFile> files, IEnumerable<string> directories, WindowsCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(options);

        var included = new List<MediaSourceFile>();
        var excluded = new List<MediaSourceFile>();
        foreach (var file in files)
        {
            (MediaExclusions.IsExcluded(file.Path) ? excluded : included).Add(file);
        }

        var items = included
            .Select(file => ToItem(file, options))
            .OrderBy(item => Tier(item))
            .ThenBy(item => item.Source.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WindowsCopyPlan(AllDirectories(directories, included), items, excluded, options.SplitPartBytes);
    }

    /// <summary>"install.swm", then "install2.swm", "install3.swm": the naming wimlib and DISM both use for the parts of a split WIM.</summary>
    public static string PartName(string firstPart, int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        if (number == 1)
        {
            return firstPart;
        }

        var extension = Path.GetExtension(firstPart);
        return firstPart[..^extension.Length] + number.ToString(CultureInfo.InvariantCulture) + extension;
    }

    internal static bool IsInstallImage(MediaSourceFile file) =>
        file.Name.Equals("install.wim", StringComparison.OrdinalIgnoreCase) || file.Name.Equals("install.esd", StringComparison.OrdinalIgnoreCase);

    private static CopyItem ToItem(MediaSourceFile file, WindowsCopyOptions options)
    {
        if (options.MaxFileBytes is not { } limit || file.Length <= limit)
        {
            return new CopyItem(file, CopyAction.Copy, file.Path);
        }

        if (options.SplitInstallImage && IsInstallImage(file))
        {
            var first = file.Directory.Length == 0 ? "install.swm" : file.Directory + "/install.swm";
            return new CopyItem(file, CopyAction.SplitInstallImage, first);
        }

        var size = SizeText.Format(file.Length);
        throw new BootrixException(ErrorCode.FileTooLargeForFileSystem, $"{file.Path} ({size}) exceeds {options.FileSystemName}")
        {
            Arguments = [options.FileSystemName, size],
        };
    }

    /// <summary>
    /// 0 for the bulk of the files, then the BIOS boot data, the EFI folders, the boot manager itself and
    /// finally the loader the UEFI fallback path looks for.
    /// </summary>
    private static int Tier(CopyItem item)
    {
        var path = item.Source.Path;
        var rootFile = !path.Contains('/');

        if (path.StartsWith("efi/boot/", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (rootFile && (path.Equals("bootmgr", StringComparison.OrdinalIgnoreCase) || path.Equals("bootmgr.efi", StringComparison.OrdinalIgnoreCase)))
        {
            return 4;
        }

        if (path.StartsWith("efi/", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (path.StartsWith("boot/", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        // The install image is by far the largest file; written last of the bulk, a failure there leaves no boot files behind.
        return IsInstallImage(item.Source) ? 1 : 0;
    }

    private static List<string> AllDirectories(IEnumerable<string> listed, List<MediaSourceFile> files)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in listed.Where(directory => !MediaExclusions.IsExcluded(directory)))
        {
            AddWithParents(all, directory);
        }

        foreach (var file in files.Where(file => file.Directory.Length > 0))
        {
            AddWithParents(all, file.Directory);
        }

        return [.. all.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static void AddWithParents(HashSet<string> set, string directory)
    {
        for (var path = directory; path.Length > 0 && set.Add(path); path = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty)
        {
        }
    }
}
