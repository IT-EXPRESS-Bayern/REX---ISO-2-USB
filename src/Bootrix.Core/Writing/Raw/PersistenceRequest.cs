// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Raw;

public enum PersistenceTable
{
    Mbr,
    Gpt,
}

/// <summary>A persistence partition to add behind an image that has been written to a disk byte for byte.</summary>
public sealed record PersistenceRequest
{
    /// <summary>Where the planner wants the partition; moved up when the image's own table reaches further than the image.</summary>
    public required long StartBytes { get; init; }

    /// <summary>The size the user asked for; reduced to what fits.</summary>
    public required long LengthBytes { get; init; }

    /// <summary>"persistence" for Debian's live-boot, "writable" for casper; also the GPT partition name.</summary>
    public required string Label { get; init; }

    public ExtFileSystemType FileSystem { get; init; } = ExtFileSystemType.Ext3;

    /// <summary>Below this the request fails instead of producing a store too small to be of use.</summary>
    public long MinimumBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Files created in the root of the new file system.</summary>
    public IReadOnlyList<ExtRootFile> Files { get; init; } = [];

    public TimeProvider? TimeProvider { get; init; }

    public static PersistenceRequest FromPlan(PlannedPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var label = partition.Label ?? "persistence";
        return new PersistenceRequest
        {
            StartBytes = partition.StartBytes,
            LengthBytes = partition.LengthBytes,
            Label = label,
            Files = PersistenceFiles.For(label),
        };
    }
}

/// <summary>What a live system expects to find inside its persistence volume.</summary>
public static class PersistenceFiles
{
    /// <summary>
    /// casper (Ubuntu and relatives) takes any volume called "writable" or "casper-rw" as it is. Debian's live-boot
    /// ignores a volume without <c>persistence.conf</c>; "/ union" keeps only the changes made to the root file system.
    /// The line feed matters: without it live-boot refuses the file.
    /// </summary>
    public static IReadOnlyList<ExtRootFile> For(string label) =>
        label is "writable" or "casper-rw" ? [] : [new ExtRootFile("persistence.conf", "/ union\n"u8.ToArray())];
}

/// <summary>Where the partition ended up and how it is registered.</summary>
public sealed record PersistenceResult(PersistenceTable Table, int Slot, long StartBytes, long LengthBytes, string Label);
