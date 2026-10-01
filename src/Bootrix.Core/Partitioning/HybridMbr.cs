// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>A GPT partition as the MBR half of a hybrid disk sees it.</summary>
public sealed record HybridEntry(byte MbrType, long StartLba, long SectorCount, bool Active = false);

/// <summary>
/// Builds a hybrid MBR: a 0xEE entry covering the GPT structures plus up to three real entries that
/// mirror GPT partitions, for BIOS-only software that cannot read GPT (DOS, old Boot Camp setups).
/// It is a deliberate violation of the specification, written with raw sector access only.
/// </summary>
public static class HybridMbr
{
    public const int MaxMirrored = Mbr.EntryCount - 1;

    /// <param name="mirrored">Partitions to expose to MBR-only software, at most three, each below 2 TiB.</param>
    /// <param name="firstUsableLba">First usable sector of the GPT; mirrored partitions must start at or after it.</param>
    /// <param name="protectiveFirst">
    /// The 0xEE entry in slot 1 is what gptfdisk and Boot Camp write; false puts it behind the
    /// mirrored entries. Exactly one 0xEE entry may exist or macOS reads the disk as plain MBR.
    /// </param>
    public static Mbr Create(
        IReadOnlyList<HybridEntry> mirrored,
        long firstUsableLba,
        bool protectiveFirst = true,
        ReadOnlySpan<byte> bootstrap = default,
        uint diskSignature = 0,
        ChsGeometry? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(mirrored);
        if (mirrored.Count is 0 or > MaxMirrored)
        {
            throw new ArgumentException($"A hybrid MBR mirrors between 1 and {MaxMirrored} partitions.", nameof(mirrored));
        }

        var builder = new MbrBuilder().WithSignature(diskSignature);
        if (!bootstrap.IsEmpty)
        {
            builder.WithBootstrap(bootstrap);
        }

        if (geometry is { } chosen)
        {
            builder.WithGeometry(chosen);
        }

        // Like gptfdisk, the 0xEE entry runs from LBA 1 up to the first mirrored partition, so GPT
        // partitions in front of it (an ESP) show up as used space instead of free.
        var protectiveSectors = mirrored.Min(entry => entry.StartLba) - 1;
        if (protectiveFirst)
        {
            builder.AddPartition(MbrPartitionType.GptProtective, 1, protectiveSectors);
        }

        foreach (var entry in mirrored)
        {
            if (entry.MbrType == MbrPartitionType.GptProtective)
            {
                throw new ArgumentException("Mirrored partitions cannot use the protective type.", nameof(mirrored));
            }

            if (entry.StartLba < firstUsableLba)
            {
                throw new ArgumentException($"A partition at LBA {entry.StartLba} starts inside the GPT structures.", nameof(mirrored));
            }

            builder.AddPartition(entry.MbrType, entry.StartLba, entry.SectorCount, entry.Active);
        }

        if (!protectiveFirst)
        {
            builder.AddPartition(MbrPartitionType.GptProtective, 1, protectiveSectors);
        }

        return builder.Build();
    }
}
