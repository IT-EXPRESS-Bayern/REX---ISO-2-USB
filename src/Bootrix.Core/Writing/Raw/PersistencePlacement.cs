// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Raw;

/// <summary>Where a persistence partition goes and which table entry describes it.</summary>
internal sealed record PersistencePlacement(
    PersistenceTable Table,
    int Slot,
    long StartLba,
    long SectorCount,
    Mbr Mbr,
    GptTable? Gpt);

/// <summary>
/// Looks at the partition table an image brought along and finds room for one more partition behind it. Only the
/// layouts that can be extended without guessing are accepted:
/// <list type="bullet">
/// <item>an MBR without a protective entry and with a free slot, which is what isohybrid images of Debian, Kali,
/// Ubuntu and Fedora have; Linux ignores a GPT that no protective entry announces, so the MBR is what counts;</item>
/// <item>a protective MBR with one GPT in the layout GptBuilder writes, which is then moved to the end of the disk.</item>
/// </list>
/// A full MBR, a hybrid MBR next to a GPT, a damaged or unusual GPT and 4Kn media are refused.
/// </summary>
internal static class PersistencePlacer
{
    public const int SectorSize = 512;
    private const long AlignmentSectors = 2048;
    private const long MbrSectorLimit = 1L << 32;

    public static PersistencePlacement Decide(Stream disk, long totalSectors, PersistenceRequest request)
    {
        var sector = new byte[SectorSize];
        disk.Position = 0;
        disk.ReadExactly(sector);
        if (!Mbr.TryParse(sector, out var mbr))
        {
            throw Unsupported("the first sector has no MBR signature");
        }

        return mbr.Entries.Any(entry => entry.Type == MbrPartitionType.GptProtective)
            ? PlaceInGpt(disk, totalSectors, request, mbr)
            : PlaceInMbr(totalSectors, request, mbr);
    }

    private static PersistencePlacement PlaceInMbr(long totalSectors, PersistenceRequest request, Mbr mbr)
    {
        // xorriso marks the partition that spans its ISO with type 0 and a size, so only a slot without a size is free.
        var slot = -1;
        for (var i = 0; i < mbr.Entries.Count && slot < 0; i++)
        {
            var entry = mbr.Entries[i];
            if (entry.Type == MbrPartitionType.Empty && entry.SectorCount == 0 && entry.StartLba == 0)
            {
                slot = i;
            }
        }

        if (slot < 0)
        {
            throw Unsupported("all four slots of the MBR are in use");
        }

        var firstFree = mbr.Entries.Where(entry => entry.Type != MbrPartitionType.Empty || entry.SectorCount != 0)
            .Select(entry => entry.EndLba)
            .DefaultIfEmpty(1)
            .Max();
        var (start, count) = Fit(request, firstFree, Math.Min(totalSectors, MbrSectorLimit));
        return new PersistencePlacement(PersistenceTable.Mbr, slot, start, count, mbr, null);
    }

    private static PersistencePlacement PlaceInGpt(Stream disk, long totalSectors, PersistenceRequest request, Mbr mbr)
    {
        if (!GptRelocation.IsPlainProtective(mbr))
        {
            throw Unsupported("the MBR is a hybrid: a protective entry next to real partitions");
        }

        if (GptTable.TryRead(disk, out var gpt) is { } problem)
        {
            throw Unsupported(problem);
        }

        if (gpt!.NextSlot >= GptBuilder.EntryCount)
        {
            throw Unsupported("all entries of the GPT are in use");
        }

        var firstFree = gpt.Partitions.Select(p => p.Partition.LastLba + 1).DefaultIfEmpty(gpt.FirstUsableLba).Max();
        var (start, count) = Fit(request, firstFree, GptTable.LastUsableLba(totalSectors) + 1);
        return new PersistencePlacement(PersistenceTable.Gpt, gpt.NextSlot, start, count, mbr, gpt);
    }

    /// <summary>Starts at the planned place or behind the last partition, whichever is later, and takes as much as fits below <paramref name="limitLba"/>.</summary>
    private static (long Start, long Count) Fit(PersistenceRequest request, long firstFreeLba, long limitLba)
    {
        var planned = request.StartBytes / SectorSize;
        var start = planned >= firstFreeLba ? planned : PartitionAlignment.AlignUp(firstFreeLba, AlignmentSectors);
        var room = PartitionAlignment.AlignDown(limitLba, AlignmentSectors) - start;
        var count = PartitionAlignment.AlignDown(Math.Min(request.LengthBytes / SectorSize, room), AlignmentSectors);
        if (count * SectorSize < request.MinimumBytes)
        {
            throw new BootrixException(
                ErrorCode.PersistenceTooSmall,
                $"{Math.Max(count, 0) * SectorSize} bytes are free behind the image")
            {
                Arguments = [SizeText.Format(request.LengthBytes), SizeText.Format(request.MinimumBytes)],
            };
        }

        return (start, count);
    }

    private static BootrixException Unsupported(string reason) =>
        new(ErrorCode.PersistenceLayoutUnsupported, reason) { Arguments = [reason] };
}
