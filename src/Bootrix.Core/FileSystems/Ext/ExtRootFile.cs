// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>A regular file created in the root directory, owned by root.</summary>
public sealed record ExtRootFile(string Name, ReadOnlyMemory<byte> Content)
{
    /// <summary>Permission bits without the file type, 0644 by default.</summary>
    public int Permissions { get; init; } = 0b110_100_100;
}
