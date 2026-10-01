// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

/// <summary>
/// A target that is written and read in whole sectors at sector-aligned offsets. The physical
/// disk implements this over unbuffered I/O; tests use a plain file.
/// </summary>
public interface IBlockDevice : IDisposable
{
    string Name { get; }

    int SectorSize { get; }

    long Length { get; }

    /// <summary>Alignment (in bytes) the caller's buffers must satisfy.</summary>
    int BufferAlignment { get; }

    void Write(long offset, ReadOnlySpan<byte> data);

    int Read(long offset, Span<byte> buffer);

    void Flush();
}
