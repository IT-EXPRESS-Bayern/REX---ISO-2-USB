// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Storage;

/// <summary>A disk image file used as a block device: backups, virtual disks in tests, image-to-image conversion.</summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    public FileBlockDevice(string path, long length, int sectorSize = 512, bool create = true)
    {
        Name = path;
        SectorSize = sectorSize;
        Length = length;
        _handle = File.OpenHandle(
            path,
            create ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        if (create && RandomAccess.GetLength(_handle) < length)
        {
            RandomAccess.SetLength(_handle, length);
        }
    }

    public string Name { get; }

    public int SectorSize { get; }

    public long Length { get; }

    public int BufferAlignment => 1;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset % SectorSize != 0 || data.Length % SectorSize != 0)
        {
            throw new ArgumentException("Writes must be sector aligned.");
        }

        if (offset + data.Length > Length)
        {
            throw new IOException("Write beyond the end of the device.");
        }

        RandomAccess.Write(_handle, data, offset);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset % SectorSize != 0 || buffer.Length % SectorSize != 0)
        {
            throw new ArgumentException("Reads must be sector aligned.");
        }

        var available = (int)Math.Min(buffer.Length, Math.Max(0, Length - offset));
        return RandomAccess.Read(_handle, buffer[..available], offset);
    }

    public void Flush() => RandomAccess.FlushToDisk(_handle);

    public void Dispose() => _handle.Dispose();
}
