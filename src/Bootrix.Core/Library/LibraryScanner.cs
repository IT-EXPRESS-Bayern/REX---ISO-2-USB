// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bootrix.Core.Library;

/// <summary>
/// Reads one library folder and checks what it finds. The folder may be a share that any workstation writes to, and
/// metadata may be half-written, hand-edited or stale, so an entry is only produced when the sidecar is valid, its
/// names are the ones the library would have chosen itself, and the image exists with the recorded size.
/// </summary>
internal static class LibraryScanner
{
    public static async Task<(List<LibraryEntry> Entries, List<LibraryProblem> Problems)> ScanAsync(
        string directory,
        LibraryLocation location,
        CancellationToken cancellationToken)
    {
        var entries = new List<LibraryEntry>();
        var problems = new List<LibraryProblem>();

        if (!Directory.Exists(directory))
        {
            // A local folder that does not exist yet is just an empty library; a share that is gone is worth telling about.
            if (location == LibraryLocation.Shared)
            {
                problems.Add(new LibraryProblem(location, directory, LibraryProblemKind.FolderUnavailable, "the folder cannot be reached"));
            }

            return (entries, problems);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new LibraryProblem(location, directory, LibraryProblemKind.FolderUnavailable, ex.Message));
            return (entries, problems);
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files.Where(f => LibraryFiles.IsMetadataName(Path.GetFileName(f))).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (entry, problem, image) = await InspectAsync(directory, location, path, cancellationToken).ConfigureAwait(false);
            if (image is not null)
            {
                claimed.Add(image);
            }

            if (entry is not null)
            {
                entries.Add(entry);
            }
            else
            {
                problems.Add(problem!);
            }
        }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (LibraryFiles.IsImageName(name) && !claimed.Contains(name))
            {
                problems.Add(new LibraryProblem(location, path, LibraryProblemKind.Unindexed, "the image has no metadata"));
            }
        }

        entries.Sort((a, b) => b.LastUsedUtc != a.LastUsedUtc ? b.LastUsedUtc.CompareTo(a.LastUsedUtc) : string.CompareOrdinal(a.Sha256, b.Sha256));
        return (entries, problems);
    }

    /// <summary>Looks at the one entry for a hash without reading the whole folder; null if the folder has no metadata for it.</summary>
    public static async Task<LibraryEntry?> FindAsync(string directory, LibraryLocation location, string sha256, CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(directory, LibraryFiles.MetadataName(sha256));
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        return (await InspectAsync(directory, location, metadataPath, cancellationToken).ConfigureAwait(false)).Entry;
    }

    private static async Task<(LibraryEntry? Entry, LibraryProblem? Problem, string? Image)> InspectAsync(
        string directory,
        LibraryLocation location,
        string metadataPath,
        CancellationToken cancellationToken)
    {
        LibraryProblem Problem(LibraryProblemKind kind, string detail, string path) => new(location, path, kind, detail);

        LibraryMetadata metadata;
        try
        {
            metadata = await LibraryMetadataFile.ReadAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (null, Problem(LibraryProblemKind.UnreadableMetadata, ex.Message, metadataPath), null);
        }

        var sha256 = Path.GetFileNameWithoutExtension(metadataPath);
        if (metadata.Schema != LibraryMetadata.CurrentSchema)
        {
            return (null, Problem(LibraryProblemKind.InvalidMetadata, $"unsupported schema {metadata.Schema}", metadataPath), null);
        }

        if (!string.Equals(metadata.Sha256, sha256, StringComparison.Ordinal))
        {
            return (null, Problem(LibraryProblemKind.InvalidMetadata, "the hash inside the file is not the one in its name", metadataPath), null);
        }

        // The name decides what is opened. A value like "../x.iso" or "C:\x.iso" never gets as far as Path.Combine.
        if (!LibraryFiles.IsImageNameFor(metadata.File, sha256))
        {
            return (null, Problem(LibraryProblemKind.InvalidMetadata, "the image name is not the one a library would have chosen for this hash", metadataPath), null);
        }

        if (metadata.Size < 0 || metadata.DownloadedUtc is not { } downloaded || metadata.LastUsedUtc is not { } lastUsed)
        {
            return (null, Problem(LibraryProblemKind.InvalidMetadata, "size or timestamps are missing", metadataPath), metadata.File);
        }

        var imagePath = Path.Combine(directory, metadata.File!);
        var image = new FileInfo(imagePath);
        if (!image.Exists)
        {
            return (null, Problem(LibraryProblemKind.MissingImage, "the image file is gone", imagePath), metadata.File);
        }

        if (image.LinkTarget is not null)
        {
            return (null, Problem(LibraryProblemKind.LinkNotFollowed, "the image is a link", imagePath), metadata.File);
        }

        if (image.Length != metadata.Size)
        {
            return (null, Problem(LibraryProblemKind.SizeMismatch, $"{image.Length} bytes on disk, {metadata.Size} recorded", imagePath), metadata.File);
        }

        var entry = new LibraryEntry
        {
            Sha256 = sha256,
            Size = metadata.Size,
            Path = imagePath,
            Location = location,
            Info = metadata.ToInfo(),
            DownloadedUtc = downloaded,
            LastUsedUtc = lastUsed,
        };

        return (entry, null, metadata.File);
    }
}
