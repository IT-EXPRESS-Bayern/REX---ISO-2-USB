// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Wim;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Windows;

internal delegate Task<IReadOnlyList<string>> SplitInstallImage(
    string source, string firstPart, long maxPartBytes, string scratchDirectory, IProgress<double>? progress, CancellationToken cancellationToken);

/// <summary>
/// Carries out a <see cref="WindowsCopyPlan"/>: creates the directories, copies the files and writes the
/// install image as .swm parts where it does not fit. The target is any directory, which on Windows is the
/// root of a volume given by its GUID path.
/// </summary>
public sealed class WindowsMediaCopier(IWindowsMediaSource source, string scratchDirectory, ILogger? logger = null)
{
    /// <summary>Block size of the copy; large enough that USB sticks see long sequential writes.</summary>
    public const int BufferBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger _log = logger ?? NullLogger.Instance;

    /// <summary>Replaceable so that tests can make the direct split fail.</summary>
    internal SplitInstallImage Splitter { get; set; } = InstallImageSplitter.SplitAsync;

    /// <summary>
    /// Copies everything onto <paramref name="destinationRoot"/>.
    /// </summary>
    /// <param name="computeHashes">Record the SHA-256 of every file, for <see cref="MediaVerifier"/>. Without it the returned list is empty.</param>
    /// <returns>What was written, in the order it was written.</returns>
    public async Task<IReadOnlyList<CopiedFile>> CopyAsync(
        WindowsCopyPlan plan,
        string destinationRoot,
        bool computeHashes,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(destinationRoot);

        if (plan.Excluded.Count > 0)
        {
            _log.LogInformation("Leaving out {Count} entries that belong to the volume of the image, not to the image", plan.Excluded.Count);
        }

        if (!Directory.Exists(destinationRoot))
        {
            Directory.CreateDirectory(destinationRoot);
        }

        foreach (var directory in plan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DestinationPath(destinationRoot, directory));
        }

        var run = new Run(progress);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Current = item.Destination;
            if (item.Action == CopyAction.SplitInstallImage)
            {
                await SplitAsync(run, plan, item, destinationRoot, computeHashes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyFileAsync(run, item, destinationRoot, computeHashes, cancellationToken).ConfigureAwait(false);
            }

            run.Report(force: true);
        }

        return run.Files;
    }

    /// <summary>Joins a '/'-separated relative path to the root with the separator the platform wants; \\?\ paths are not normalised by .NET, so the slashes must be right.</summary>
    internal static string DestinationPath(string root, string relative)
    {
        var separator = Path.DirectorySeparatorChar;
        return root.TrimEnd('\\', '/') + separator + relative.Replace('/', separator);
    }

    private async Task CopyFileAsync(Run run, CopyItem item, string root, bool hash, CancellationToken cancellationToken)
    {
        var destination = DestinationPath(root, item.Destination);
        await using var input = source.OpenRead(item.Source.Path);
        await using (var output = CreateTarget(destination, item.Source.Length))
        {
            var (bytes, sha) = await run.Pump.CopyAsync(input, output, hash, run.Advance, cancellationToken).ConfigureAwait(false);
            if (bytes != item.Source.Length)
            {
                throw new BootrixException(ErrorCode.ImageUnreadable, $"{item.Source.Path}: {item.Source.Length} bytes expected, {bytes} read")
                {
                    Arguments = [item.Source.Path],
                };
            }

            if (item.Source.LastWriteUtc is { } modified)
            {
                File.SetLastWriteTimeUtc(output.SafeFileHandle, modified);
            }

            if (sha is not null)
            {
                run.Files.Add(new CopiedFile(item.Destination, bytes, sha));
            }
        }
    }

    private async Task SplitAsync(Run run, WindowsCopyPlan plan, CopyItem item, string root, bool hash, CancellationToken cancellationToken)
    {
        var firstPart = DestinationPath(root, item.Destination);
        var needsExtraction = source.LocalPath(item.Source.Path) is null;

        // Phases that share the bytes of the item: copy the image out of the ISO stream, cut it, read the parts back.
        var phases = (needsExtraction ? 1 : 0) + 1 + (hash ? 1 : 0);
        var start = run.Done;
        var phase = 0;
        IProgress<double> Slice() => run.Fraction(start, item.Bytes, phase++, phases);

        string? extracted = null;
        try
        {
            var local = source.LocalPath(item.Source.Path);
            if (local is null)
            {
                extracted = await ExtractAsync(run, item, Slice(), cancellationToken).ConfigureAwait(false);
                local = extracted;
            }

            var splitProgress = Slice();
            IReadOnlyList<string> parts;
            var hashed = false;
            try
            {
                parts = await Splitter(local, firstPart, plan.SplitPartBytes, scratchDirectory, splitProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is BootrixException { Code: ErrorCode.ExternalToolFailed })
            {
                // wimlib writes through its own file layer; a target that it cannot open is not necessarily one that cannot be written.
                _log.LogWarning(ex, "Splitting {Image} straight onto the target failed; splitting in the scratch folder and copying the parts", item.Source.Path);
                InstallImageSplitter.DeleteParts(firstPart);
                var copied = await SplitViaScratchAsync(run, plan, item, local, firstPart, hash, splitProgress, cancellationToken).ConfigureAwait(false);
                parts = copied.Parts;
                hashed = hash;
                run.Files.AddRange(copied.Files);
            }

            if (hash && !hashed)
            {
                await HashPartsAsync(run, item, parts, Slice(), cancellationToken).ConfigureAwait(false);
            }

            _log.LogInformation("{Image} written as {Count} part(s)", item.Source.Path, parts.Count);
            run.CompleteTo(start + item.Bytes);
        }
        finally
        {
            if (extracted is not null)
            {
                File.Delete(extracted);
            }
        }
    }

    /// <summary>wimlib wants a file name, so an install image that only exists inside an ISO stream is copied out first.</summary>
    private async Task<string> ExtractAsync(Run run, CopyItem item, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scratchDirectory);
        InstallImageSplitter.EnsureFreeSpace(scratchDirectory, item.Source.Length);

        var path = Path.Combine(scratchDirectory, "extract-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(item.Source.Path));
        await using var input = source.OpenRead(item.Source.Path);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long done = 0;
        var (bytes, _) = await run.Pump.CopyAsync(
            input,
            output,
            hash: false,
            count =>
            {
                done += count;
                progress.Report((double)done / Math.Max(1, item.Source.Length));
            },
            cancellationToken).ConfigureAwait(false);

        if (bytes != item.Source.Length)
        {
            throw new BootrixException(ErrorCode.ImageUnreadable, $"{item.Source.Path}: {item.Source.Length} bytes expected, {bytes} read")
            {
                Arguments = [item.Source.Path],
            };
        }

        return path;
    }

    private async Task<(IReadOnlyList<string> Parts, List<CopiedFile> Files)> SplitViaScratchAsync(
        Run run, WindowsCopyPlan plan, CopyItem item, string local, string firstPart, bool hash, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(scratchDirectory, "parts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var scratchFirst = Path.Combine(folder, Path.GetFileName(firstPart));
            var scratchParts = await Splitter(local, scratchFirst, plan.SplitPartBytes, scratchDirectory, progress, cancellationToken).ConfigureAwait(false);

            var parts = new List<string>();
            var files = new List<CopiedFile>();
            for (var index = 0; index < scratchParts.Count; index++)
            {
                var relative = WindowsCopyPlan.PartName(item.Destination, index + 1);
                var target = DestinationPath(Path.GetDirectoryName(firstPart)!, Path.GetFileName(relative));
                await using var input = new FileStream(scratchParts[index], FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = CreateTarget(target, input.Length);
                var (bytes, sha) = await run.Pump.CopyAsync(input, output, hash, _ => { }, cancellationToken).ConfigureAwait(false);
                parts.Add(target);
                if (sha is not null)
                {
                    files.Add(new CopiedFile(relative, bytes, sha));
                }
            }

            return (parts, files);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Reads the parts once right after they were written. The values are compared with a second read after
    /// the volume has been remounted, which exposes what the cache of Windows or the stick made up in between.
    /// </summary>
    private static async Task HashPartsAsync(Run run, CopyItem item, IReadOnlyList<string> parts, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var total = parts.Sum(part => new FileInfo(part).Length);
        long done = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            await using var input = new FileStream(parts[index], FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha = await run.Pump.HashAsync(
                input,
                count =>
                {
                    done += count;
                    progress.Report((double)done / Math.Max(1, total));
                },
                cancellationToken).ConfigureAwait(false);
            run.Files.Add(new CopiedFile(WindowsCopyPlan.PartName(item.Destination, index + 1), input.Length, sha));
        }
    }

    private static FileStream CreateTarget(string path, long length) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,

            // Claims the clusters up front: a full stick fails here instead of after gigabytes, and the file stays in one piece.
            PreallocationSize = length,
        });

    private sealed class Run(IProgress<CopyProgress>? progress)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();

        public StreamPump Pump { get; } = new(BufferBytes);

        public List<CopiedFile> Files { get; } = [];

        public long Done { get; private set; }

        public string? Current { get; set; }

        public void Advance(int bytes)
        {
            Done += bytes;
            Report(force: false);
        }

        public void CompleteTo(long done)
        {
            if (done > Done)
            {
                Done = done;
            }
        }

        public void Report(bool force)
        {
            if (!force && _sinceReport.Elapsed < ReportInterval)
            {
                return;
            }

            _sinceReport.Restart();
            progress?.Report(new CopyProgress(Done, Current));
        }

        /// <summary>Progress of one phase of an item, as a share of the item's bytes. Never moves backwards.</summary>
        public IProgress<double> Fraction(long start, long bytes, int phase, int phases) => new PhaseProgress(this, start, bytes, phase, phases);

        private sealed class PhaseProgress(Run run, long start, long bytes, int phase, int phases) : IProgress<double>
        {
            public void Report(double value)
            {
                var overall = (phase + Math.Clamp(value, 0, 1)) / phases;
                var target = start + (long)(bytes * overall);
                if (target > run.Done)
                {
                    run.Done = target;
                    run.Report(force: false);
                }
            }
        }
    }
}
