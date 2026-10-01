// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

public sealed record ExtFormatResult(
    ExtFileSystemType Type,
    Guid Uuid,
    int BlockSize,
    long BlockCount,
    int GroupCount,
    int InodesPerGroup,
    long JournalBlocks);
