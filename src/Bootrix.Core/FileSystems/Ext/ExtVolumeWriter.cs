// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>
/// Adds files to the root directory of an existing ext2/3/4 volume. Meant for volumes just created by
/// <see cref="ExtFormatter"/> (persistence.conf, casper-rw stores); it is not a general file system driver.
/// Volumes using flex_bg, 64bit, meta_bg or metadata checksums are rejected.
/// </summary>
public sealed class ExtVolumeWriter
{
    private readonly Stream _stream;
    private readonly TimeProvider _clock;
    private ExtVolume _volume;

    private ExtVolumeWriter(Stream stream, TimeProvider clock, ExtVolume volume)
    {
        _stream = stream;
        _clock = clock;
        _volume = volume;
    }

    public static ExtVolumeWriter Open(Stream stream, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var clock = timeProvider ?? TimeProvider.System;
        return new ExtVolumeWriter(stream, clock, ExtVolume.Open(stream, clock));
    }

    public void AddRootFile(string name, ReadOnlyMemory<byte> content) => AddRootFile(new ExtRootFile(name, content));

    /// <summary>
    /// Creates the file and writes the updated bitmaps, descriptors and superblocks before returning, so the
    /// volume is consistent after every call. A failed call leaves the volume as it was.
    /// </summary>
    public void AddRootFile(ExtRootFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            ExtRootDirectory.AddFile(_volume, file);
            _volume.Flush();
        }
        catch
        {
            // The in-memory bitmaps and counters may already include this file's allocations; start over from disk.
            _volume = ExtVolume.Open(_stream, _clock);
            throw;
        }
    }
}
