// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Writing.Dos;
using Bootrix.Windows.Tools;

namespace Bootrix.Windows.Writing.Dos;

/// <summary>
/// What the steps of one DOS job hand to each other: the system that was assembled for every target. Targets can differ in size
/// and in whether old-BIOS fixes apply, so each has its own. Created per job by the writer; steps run one after another.
/// </summary>
internal sealed class DosJobState
{
    private readonly Dictionary<MediaWriteTarget, DosSystem> _systems = [];

    public DosSystem SystemOf(MediaWriteTarget target) =>
        _systems.TryGetValue(target, out var system) ? system : throw new InvalidOperationException("The DOS system has not been prepared yet.");

    /// <summary>The target a partition of one of the plans belongs to; the formatter hook only gets the partition.</summary>
    public static MediaWriteTarget TargetOf(MediaWriteContext write, PlannedPartition partition) =>
        write.Targets.First(target => target.Plan.Partitions.Any(candidate => ReferenceEquals(candidate, partition)));

    /// <summary>Assembles the system for every target. MS-DOS needs diskcopy.dll first, which is downloaded here if the user agreed to it.</summary>
    public async Task PrepareAsync(MediaWriteContext write, JobContext context, CancellationToken cancellationToken)
    {
        var options = write.Spec.Dos;
        byte[]? diskcopy = null;
        if (options.Flavor == DosFlavor.MsDos)
        {
            foreach (var target in write.Targets)
            {
                DosMedium.EnsureMsDosFits(target.Plan);
            }

            diskcopy = await FetchDiskcopyAsync(options, context, cancellationToken).ConfigureAwait(false);
        }

        foreach (var target in write.Targets)
        {
            _systems[target] = DosMedium.CreateSystem(options, target.Plan, diskcopy);
        }
    }

    private static async Task<byte[]> FetchDiskcopyAsync(DosOptions options, JobContext context, CancellationToken cancellationToken)
    {
        var folder = OscdimgLocator.ToolFolder;
        if (options.AcceptMicrosoftDownload)
        {
            // Only SYSTEM and administrators may change what is kept there; the file is read back by the elevated process later.
            OscdimgLocator.EnsureProtectedFolder(folder);
        }

        using var http = new HttpClient();
        var fetcher = new MsDosFetcher(http, new AuthenticodeSignatureVerifier(), folder);
        return await fetcher.GetAsync(options.AcceptMicrosoftDownload, new Progress<double>(value => context.ReportStep(value)), cancellationToken).ConfigureAwait(false);
    }
}
