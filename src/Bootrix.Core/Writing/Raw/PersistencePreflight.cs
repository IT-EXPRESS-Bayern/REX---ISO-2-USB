// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Disk;

namespace Bootrix.Core.Writing.Raw;

/// <summary>
/// Tells from the table the inspector read out of the image whether a persistence partition can be added behind
/// it, so that a layout which is refused later is known before the first byte is written. The installer repeats the
/// check on the real disk and has the last word.
/// </summary>
public static class PersistencePreflight
{
    /// <summary>The reason persistence cannot be added to an image with this table, or null when nothing speaks against it.</summary>
    public static string? Problem(DiskLayout? layout)
    {
        if (layout is null)
        {
            return null;
        }

        if (!layout.HasMbrSignature)
        {
            return "the first sector has no MBR signature";
        }

        if (!layout.MbrPartitions.Any(partition => partition.IsProtective))
        {
            return layout.MbrPartitions.Count >= 4 ? "all four slots of the MBR are in use" : null;
        }

        if (layout.MbrPartitions.Count != 1)
        {
            return "the MBR is a hybrid: a protective entry next to real partitions";
        }

        if (!layout.HasGpt || !layout.GptHeaderValid)
        {
            return "the GPT is missing or damaged";
        }

        return layout.GptSectorSize == 512 ? null : "the GPT is written for 4096-byte sectors";
    }
}
