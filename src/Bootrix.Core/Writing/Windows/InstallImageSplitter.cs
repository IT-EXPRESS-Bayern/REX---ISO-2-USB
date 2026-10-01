// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Wim;
using WimCompression = Bootrix.Core.Wim.WimCompression;
using Bootrix.Core.Planning;
using Bootrix.Core.Wim;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// Cuts an install.wim into the .swm parts that fit FAT32. Windows setup finds them by name
/// (install.swm, install2.swm, ...) and reassembles the image itself.
/// </summary>
public static class InstallImageSplitter
{
    /// <summary>Space the LZX copy of an ESD takes, relative to the ESD: solid LZMS packs a good third tighter.</summary>
    private const double ConversionGrowth = 1.5;

    /// <summary>
    /// Writes <paramref name="firstPart"/> and its successors next to each other. A solid image (ESD) cannot be
    /// split by wimlib, so it is first exported as an ordinary LZX image into <paramref name="scratchDirectory"/>.
    /// </summary>
    /// <returns>The paths of all parts in order.</returns>
    /// <exception cref="BootrixException">wimlib is missing, the scratch space is too small or wimlib refuses the image.</exception>
    public static Task<IReadOnlyList<string>> SplitAsync(
        string source,
        string firstPart,
        long maxPartBytes,
        string scratchDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(firstPart);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPartBytes, 1L << 20);

        if (!WimFile.IsAvailable)
        {
            throw new BootrixException(ErrorCode.WimLibraryMissing, "wimlib could not be loaded");
        }

        return Task.Run(() => Split(source, firstPart, maxPartBytes, scratchDirectory, progress, cancellationToken), cancellationToken);
    }

    /// <summary>The existing parts of a split image, starting with <paramref name="firstPart"/>; stops at the first gap.</summary>
    public static IReadOnlyList<string> FindParts(string firstPart)
    {
        var parts = new List<string>();
        for (var number = 1; File.Exists(WindowsCopyPlan.PartName(firstPart, number)); number++)
        {
            parts.Add(WindowsCopyPlan.PartName(firstPart, number));
        }

        return parts;
    }

    public static void DeleteParts(string firstPart)
    {
        foreach (var part in FindParts(firstPart))
        {
            File.Delete(part);
        }
    }

    private static IReadOnlyList<string> Split(
        string source, string firstPart, long maxPartBytes, string scratchDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string? converted = null;
        try
        {
            var input = source;
            var splitStart = 0.0;
            if (IsSolid(source))
            {
                converted = ConvertToLzx(source, scratchDirectory, new Scaled(progress, 0, 0.5), cancellationToken);
                input = converted;
                splitStart = 0.5;
            }

            try
            {
                using var wim = WimFile.Open(input);
                wim.Split(firstPart, maxPartBytes, new Scaled(progress, splitStart, 1), cancellationToken);
            }
            catch
            {
                // Leave no half-written set behind; parts without their siblings only confuse setup.
                DeleteParts(firstPart);
                throw;
            }

            progress?.Report(1);
            return FindParts(firstPart);
        }
        finally
        {
            if (converted is not null)
            {
                File.Delete(converted);
            }
        }
    }

    private static string ConvertToLzx(string source, string scratchDirectory, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scratchDirectory);
        var needed = (long)(new FileInfo(source).Length * ConversionGrowth);
        EnsureFreeSpace(scratchDirectory, needed);

        var converted = Path.Combine(scratchDirectory, "install-" + Guid.NewGuid().ToString("N")[..8] + ".wim");
        try
        {
            using var esd = WimFile.Open(source);
            esd.WriteImage(converted, WimLibNative.AllImages, WimCompression.Lzx, recompress: true, solid: false, progress, cancellationToken);
            return converted;
        }
        catch
        {
            File.Delete(converted);
            throw;
        }
    }

    private static bool IsSolid(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        return WimMetadata.Read(stream).Header.IsSolid;
    }

    internal static void EnsureFreeSpace(string directory, long neededBytes)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A drive that cannot be asked is not proof that it is full; the write itself will tell.
            return;
        }

        if (free < neededBytes)
        {
            throw new BootrixException(ErrorCode.InsufficientSpace, $"{directory}: {free} bytes free, {neededBytes} needed")
            {
                Arguments = [directory, SizeText.Format(neededBytes)],
            };
        }
    }

    /// <summary>Maps the 0 to 1 progress of one phase onto a slice of the whole operation.</summary>
    private sealed class Scaled(IProgress<double>? target, double from, double to) : IProgress<double>
    {
        public void Report(double value) => target?.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
