// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// The volume stored in a .sparsebundle (UDSB): a directory with an Info.plist that gives the volume size and
/// band size, and a bands folder with one file per band named by its hexadecimal index.
/// Missing band files and the unwritten tail of a band read as zeros.
/// </summary>
public sealed class SparseBundleStream : ReadOnlyVolumeStream
{
    private const string BundleType = "com.apple.diskimage.sparsebundle";
    private const long MaxVolumeBytes = 16L * 1024 * 1024 * 1024 * 1024;
    private const long MaxInfoBytes = 1024 * 1024;
    private const long MinBandBytes = 512;
    private const long MaxBandBytes = 4L * 1024 * 1024 * 1024;

    private readonly string _bandDirectory;
    private readonly long _bandBytes;
    private SafeFileHandle? _bandHandle;
    private long _bandIndex = -1;

    private SparseBundleStream(string bandDirectory, long length, long bandBytes)
        : base(length)
    {
        _bandDirectory = bandDirectory;
        _bandBytes = bandBytes;
    }

    public long BandSize => _bandBytes;

    public static SparseBundleStream Open(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);

        var plistPath = Path.Combine(bundlePath, "Info.plist");
        byte[] plist;
        try
        {
            if (new FileInfo(plistPath).Length > MaxInfoBytes)
            {
                throw ImageErrors.Corrupt("Info.plist is implausibly large");
            }

            plist = File.ReadAllBytes(plistPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageErrors.Unreadable($"{plistPath}: {ex.Message}", ex);
        }

        var info = PlistReader.ParseDictionary(plist);
        if (info.GetValueOrDefault("diskimage-bundle-type") is string type && type != BundleType)
        {
            throw ImageErrors.Unsupported($"disk image bundle type {type}");
        }

        var size = ReadInteger(info, "size");
        var bandSize = ReadInteger(info, "band-size");
        if (size is <= 0 or > MaxVolumeBytes || bandSize is < MinBandBytes or > MaxBandBytes)
        {
            throw ImageErrors.Corrupt("sparse bundle declares an invalid size or band size");
        }

        return new SparseBundleStream(Path.Combine(bundlePath, "bands"), size, bandSize);
    }

    protected override int ReadCore(long position, Span<byte> destination)
    {
        var band = position / _bandBytes;
        var offsetInBand = position % _bandBytes;
        var count = (int)Math.Min(destination.Length, _bandBytes - offsetInBand);
        destination = destination[..count];

        var handle = SelectBand(band);
        var read = 0;
        if (handle is not null)
        {
            while (read < count)
            {
                var n = RandomAccess.Read(handle, destination[read..], offsetInBand + read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }

        destination[read..].Clear();
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bandHandle?.Dispose();
            _bandHandle = null;
        }

        base.Dispose(disposing);
    }

    private static long ReadInteger(Dictionary<string, object?> info, string key) => info.GetValueOrDefault(key) switch
    {
        long number => number,
        string text when long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
        _ => throw ImageErrors.Corrupt($"Info.plist has no valid '{key}' entry"),
    };

    // Keeps the most recent band open: sequential reads touch each band file many times.
    private SafeFileHandle? SelectBand(long band)
    {
        if (band == _bandIndex)
        {
            return _bandHandle;
        }

        _bandHandle?.Dispose();
        _bandHandle = null;
        _bandIndex = band;

        var path = Path.Combine(_bandDirectory, band.ToString("x", CultureInfo.InvariantCulture));
        try
        {
            _bandHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // An absent band was never written.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageErrors.Unreadable($"{path}: {ex.Message}", ex);
        }

        return _bandHandle;
    }
}
