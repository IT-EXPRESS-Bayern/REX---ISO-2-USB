// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Storage;

/// <summary>
/// A physical disk opened for sector I/O. Writes bypass the system cache and go through to the
/// device, so a completed write really is on the stick (as far as the stick's controller admits).
/// </summary>
public sealed class PhysicalDisk : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    internal PhysicalDisk(SafeFileHandle handle, string name, int sectorSize, long length)
    {
        _handle = handle;
        Name = name;
        SectorSize = sectorSize;
        Length = length;
    }

    public string Name { get; }

    public int SectorSize { get; }

    public long Length { get; }

    public int BufferAlignment => Math.Max(SectorSize, 4096);

    internal SafeFileHandle Handle => _handle;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        EnsureAligned(offset, data.Length);
        RandomAccess.Write(_handle, data, offset);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        EnsureAligned(offset, buffer.Length);
        return RandomAccess.Read(_handle, buffer, offset);
    }

    public void Flush()
    {
        if (!Kernel32.FlushFileBuffers(_handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "FlushFileBuffers failed");
        }
    }

    public void Dispose() => _handle.Dispose();

    private void EnsureAligned(long offset, int length)
    {
        if (offset % SectorSize != 0 || length % SectorSize != 0)
        {
            throw new ArgumentException($"Unbuffered I/O needs sector alignment (offset {offset}, length {length}, sector {SectorSize}).");
        }
    }
}
