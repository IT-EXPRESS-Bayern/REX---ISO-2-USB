// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>
/// Every sector range a GPT occupies, ready to be written to a disk: the (protective) MBR, the
/// primary header and entry array after it, and their copies at the end of the disk.
/// </summary>
public sealed class GptImage
{
    public const long PrimaryHeaderLba = 1;
    public const long PrimaryEntriesLba = 2;

    internal GptImage(
        int sectorSize, long totalSectors, Mbr mbr, byte[] primaryHeader, byte[] backupHeader, byte[] entryArray)
    {
        SectorSize = sectorSize;
        TotalSectors = totalSectors;
        Mbr = mbr;
        PrimaryHeader = primaryHeader;
        BackupHeader = backupHeader;
        EntryArray = entryArray;
    }

    public int SectorSize { get; }

    public long TotalSectors { get; }

    /// <summary>The protective MBR, or a hybrid MBR when the caller replaced it.</summary>
    public Mbr Mbr { get; }

    /// <summary>One full sector: the 92-byte header followed by zeros.</summary>
    public ReadOnlyMemory<byte> PrimaryHeader { get; }

    public ReadOnlyMemory<byte> BackupHeader { get; }

    /// <summary>The 128 entries of 128 bytes; identical for both copies.</summary>
    public ReadOnlyMemory<byte> EntryArray { get; }


    public long BackupEntriesLba => BackupHeaderLba - EntrySectors;

    public long BackupHeaderLba => TotalSectors - 1;

    private long EntrySectors => (EntryArray.Length + SectorSize - 1) / SectorSize;

    /// <summary>
    /// Writes backup before primary, as UEFI requires of software that updates a GPT, and the MBR
    /// last so an interrupted write never leaves a disk that claims to be GPT without a table.
    /// </summary>
    public void WriteTo(Stream disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        WriteSectors(disk, BackupEntriesLba, EntryArray.Span);
        WriteSectors(disk, BackupHeaderLba, BackupHeader.Span);
        WriteSectors(disk, PrimaryEntriesLba, EntryArray.Span);
        WriteSectors(disk, PrimaryHeaderLba, PrimaryHeader.Span);
        WriteSectors(disk, 0, Mbr.ToBytes());
    }

    private void WriteSectors(Stream disk, long lba, ReadOnlySpan<byte> data)
    {
        // Device streams insist on whole sectors, so the entry array is padded to a sector boundary.
        var length = (data.Length + SectorSize - 1) / SectorSize * SectorSize;
        var buffer = new byte[length];
        data.CopyTo(buffer);
        disk.Position = lba * SectorSize;
        disk.Write(buffer, 0, buffer.Length);
    }
}
