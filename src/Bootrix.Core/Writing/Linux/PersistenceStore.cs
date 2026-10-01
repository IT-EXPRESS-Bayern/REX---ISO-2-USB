// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.FileSystems.Ext;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// Formats the persistence partition of a Linux stick: an ext3 file system whose label the live system looks for
/// ("writable" for casper, "persistence" for live-boot; the planner chose it) and, for live-boot, the
/// <c>persistence.conf</c> that tells it what to store.
/// </summary>
public static class PersistenceStore
{
    public const string ConfigName = "persistence.conf";

    // Debian's live-boot ignores a store without this file; "/ union" keeps only the changes to the whole system. The final
    // line feed matters: without it live-boot's parser drops the last line (Rufus notes the same).
    private static readonly byte[] LiveBootConfig = Encoding.ASCII.GetBytes("/ union\n");

    public static ExtFormatOptions OptionsFor(PlannedPartition partition, FamilyTraits traits)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(traits);

        return new ExtFormatOptions
        {
            Type = ExtFileSystemType.Ext3,
            Label = partition.Label ?? "persistence",
            RootFiles = traits.LiveBoot ? [new ExtRootFile(ConfigName, LiveBootConfig)] : [],
        };
    }

    /// <summary>Writes the file system into a stream that spans exactly the partition.</summary>
    public static ExtFormatResult Format(Stream partition, PlannedPartition planned, FamilyTraits traits, CancellationToken cancellationToken = default) =>
        ExtFormatter.Format(partition, OptionsFor(planned, traits), cancellationToken);
}
