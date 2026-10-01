// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;

namespace Bootrix.Core.Diagnostics;

/// <summary>A folder whose newest files go into the package; <paramref name="EntryPrefix"/> is the folder name inside the archive.</summary>
public sealed record DiagnosticsSource(string Folder, string Pattern, string EntryPrefix, int MaxFiles = 10);

/// <summary>
/// The archive a person sends to support: the newest log files and a short description of the system. It holds
/// no images, no passwords (they never reach a log) and nothing that identifies the person or the machine by name.
/// </summary>
public static class DiagnosticsPackage
{
    public const string SystemInfoEntry = "system-info.txt";

    /// <param name="extraArchives">Archives made elsewhere (the elevated process keeps its own logs); their entries are copied in below the given prefix.</param>
    public static void Write(
        Stream output,
        IEnumerable<DiagnosticsSource> sources,
        string systemInfo,
        IEnumerable<(string Prefix, Stream Archive)>? extraArchives = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sources);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var info = zip.CreateEntry(SystemInfoEntry, CompressionLevel.Optimal);
        using (var writer = new StreamWriter(info.Open(), new UTF8Encoding(false)))
        {
            writer.Write(systemInfo);
        }

        foreach (var source in sources)
        {
            AddNewest(zip, source);
        }

        foreach (var (prefix, archive) in extraArchives ?? [])
        {
            using var other = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in other.Entries.Where(e => e.Length > 0))
            {
                var target = zip.CreateEntry($"{prefix}/{SafeName(entry.FullName)}", CompressionLevel.Optimal);
                using var from = entry.Open();
                using var to = target.Open();
                from.CopyTo(to);
            }
        }
    }

    private static void AddNewest(ZipArchive zip, DiagnosticsSource source)
    {
        if (!Directory.Exists(source.Folder))
        {
            return;
        }

        var files = new DirectoryInfo(source.Folder)
            .EnumerateFiles(source.Pattern)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(source.MaxFiles);

        foreach (var file in files)
        {
            try
            {
                // The current log is still open for writing in the running program.
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry($"{source.EntryPrefix}/{file.Name}", CompressionLevel.Optimal);
                entry.LastWriteTime = file.LastWriteTimeUtc;
                using var target = entry.Open();
                stream.CopyTo(target);
            }
            catch (IOException)
            {
                // A file that cannot be read is left out; the rest of the package is still useful.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string SafeName(string name) => name.Replace('\\', '/').TrimStart('/').Replace("../", "", StringComparison.Ordinal);
}
