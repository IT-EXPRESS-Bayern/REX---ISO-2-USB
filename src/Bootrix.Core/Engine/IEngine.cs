// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Engine;

/// <summary>
/// The part of Bootrix that needs administrator rights: looking at raw disks and writing to them.
/// The CLI runs it in-process; the GUI talks to an elevated broker process that hosts it, so the
/// window itself, the downloader and all file parsers stay unprivileged.
/// </summary>
public interface IEngine
{
    /// <summary>Raised (debounced) when a disk appears or disappears.</summary>
    event EventHandler? DevicesChanged;

    Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the fingerprint that ties the user's confirmation to this disk in its current state.
    /// It has to happen on the engine side because it reads the partition tables of the raw disk.
    /// </summary>
    Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one job to its end. <paramref name="cancellationToken"/> asks for a stop at the next safe
    /// point, <paramref name="abortToken"/> for an immediate one.
    /// </summary>
    Task<EngineJobResult> RunJobAsync(
        EngineJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken = default);
}
