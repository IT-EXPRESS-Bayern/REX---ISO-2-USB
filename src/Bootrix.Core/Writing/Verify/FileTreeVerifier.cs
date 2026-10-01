// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Iso;

namespace Bootrix.Core.Writing.Verify;

public enum FileDifferenceKind
{
    Missing,
    SizeDiffers,
    ContentDiffers,
}

/// <param name="LikelyPatched">A boot menu or checksum list that file mode edits on purpose (volume label, persistence keyword); listed, but not counted as a failure.</param>
public sealed record FileDifference(string Path, FileDifferenceKind Kind, long ExpectedBytes, long ActualBytes, bool LikelyPatched);

public sealed record FileTreeReport(int FilesCompared, long BytesCompared, IReadOnlyList<FileDifference> Differences, IReadOnlyList<string> SplitFiles)
{
    public IEnumerable<FileDifference> Unexpected => Differences.Where(difference => !difference.LikelyPatched);

    public bool Matches => !Unexpected.Any();

    public BootrixException? ToException(string deviceName)
    {
        var unexpected = Unexpected.ToList();
        if (unexpected.Count == 0)
        {
            return null;
        }

        var shown = string.Join(", ", unexpected.Take(10).Select(difference => $"{difference.Path} ({difference.Kind})"));
        return new BootrixException(ErrorCode.VerifyFilesDiffer, $"{deviceName}: {unexpected.Count} files differ: {shown}")
        {
            Arguments = [unexpected.Count, unexpected[0].Path],
        };
    }
}

/// <summary>
/// Compares the files of an ISO with a medium that was written in file mode: every file of the image has to be there with
/// the same size and the same SHA-256. What the medium has in addition (System Volume Information, the Recycle Bin, boot
/// files Bootrix added) is ignored. An install.wim that was split into .swm parts to fit FAT32 is reported, not compared.
/// </summary>
public static class FileTreeVerifier
{
    private const int EntryLimit = 1_000_000;
    private const int MaxReported = 1000;
    private const int BufferBytes = 1024 * 1024;

    private static readonly string[] PatchedNames =
    [
        "isolinux.cfg", "syslinux.cfg", "txt.cfg", "live.cfg", "menu.cfg", "grub.cfg", "loopback.cfg", "loader.conf",
        "md5sum.txt", "sha256sum.txt",
    ];

    /// <param name="iso">A seekable ISO 9660 or UDF image.</param>
    /// <param name="volumeRoot">Root directory of the medium's file system, e.g. a volume GUID path.</param>
    public static FileTreeReport Compare(
        Stream iso,
        string volumeRoot,
        IProgress<(long Done, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);

        var volume = Iso9660Reader.Read(iso);
        var container = ImageContainerSniffer.Detect(iso);
        using var files = ImageFileSystem.OpenIso(iso, volume?.HasJoliet ?? false, container != ImageContainer.Iso9660, EntryLimit, cancellationToken)
            ?? throw new BootrixException(ErrorCode.ImageUnsupported, "the image has no file tree to compare")
            {
                Arguments = ["no file tree to compare; verify the medium byte for byte instead"],
            };

        var onMedium = IndexMedium(volumeRoot, cancellationToken);
        var total = files.Index.TotalBytes;
        var differences = new List<FileDifference>();
        var split = new List<string>();
        long done = 0;
        var compared = 0;

        foreach (var file in files.Index.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!onMedium.TryGetValue(file.Path, out var path))
            {
                if (IsSplitWim(file.Path, onMedium))
                {
                    split.Add(file.Path);
                }
                else
                {
                    Add(differences, new FileDifference(file.Path, FileDifferenceKind.Missing, file.Length, 0, false));
                }

                done += file.Length;
                continue;
            }

            compared++;
            var actual = new FileInfo(path).Length;
            if (actual != file.Length)
            {
                Add(differences, new FileDifference(file.Path, FileDifferenceKind.SizeDiffers, file.Length, actual, IsPatched(file.Path)));
            }
            else if (!SameContent(files, file.Path, path, cancellationToken))
            {
                Add(differences, new FileDifference(file.Path, FileDifferenceKind.ContentDiffers, file.Length, actual, IsPatched(file.Path)));
            }

            done += file.Length;
            progress?.Report((done, total));
        }

        return new FileTreeReport(compared, done, differences, split);
    }

    private static Dictionary<string, string> IndexMedium(string root, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            var relative = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : full;
            index[relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/')] = path;
        }

        return index;
    }

    /// <summary>File mode on FAT32 cuts install.wim into install.swm, install2.swm and so on.</summary>
    private static bool IsSplitWim(string path, Dictionary<string, string> onMedium)
    {
        if (!path.EndsWith("/install.wim", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return onMedium.ContainsKey(string.Concat(path.AsSpan(0, path.Length - ".wim".Length), ".swm"));
    }

    private static bool IsPatched(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return PatchedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (path.Contains("loader/entries/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameContent(ImageFileSystem files, string imagePath, string mediumPath, CancellationToken cancellationToken)
    {
        using var expected = files.OpenFile(imagePath);
        if (expected is null)
        {
            return false;
        }

        using var actual = new FileStream(mediumPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return Hash(expected, cancellationToken).AsSpan().SequenceEqual(Hash(actual, cancellationToken));
    }

    private static byte[] Hash(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }

    private static void Add(List<FileDifference> differences, FileDifference difference)
    {
        if (differences.Count < MaxReported)
        {
            differences.Add(difference);
        }
    }
}
