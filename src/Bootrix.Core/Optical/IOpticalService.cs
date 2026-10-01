// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Reading;

namespace Bootrix.Core.Optical;

/// <summary>
/// Everything Bootrix does with optical drives. The Windows implementation sits on IMAPI2 and
/// raw volume reads; the jobs only know this interface, which is what makes them testable.
/// </summary>
public interface IOpticalService
{
    Task<IReadOnlyList<OpticalDrive>> EnumerateDrivesAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised (debounced) when a drive is added or removed.</summary>
    event EventHandler? DrivesChanged;

    /// <summary>The disc in the drive; <see cref="OpticalMedia.None"/> when the tray is empty.</summary>
    Task<OpticalMedia> QueryMediaAsync(OpticalDrive drive, CancellationToken cancellationToken = default);

    /// <summary>Writes the image byte for byte from the first sector of the disc. Needs a blank or rewritable disc.</summary>
    Task BurnImageAsync(
        OpticalDrive drive,
        DiscImageSource image,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Builds a data disc from the files of a folder and writes it.</summary>
    Task BurnFolderAsync(
        OpticalDrive drive,
        FolderBurnRequest request,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Erasing cannot be interrupted once the drive has started; cancelling takes effect only
    /// before that point.
    /// </summary>
    Task EraseAsync(
        OpticalDrive drive,
        EraseMode mode,
        IProgress<EraseProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the disc for reading 2048-byte sectors. The caller disposes the reader.</summary>
    ISectorReader OpenSectorReader(OpticalDrive drive);

    /// <summary>Reads the disc sector by sector into an ISO file; see <see cref="DiscRipper"/> for how defects are handled.</summary>
    Task<RipReport> RipToIsoAsync(
        OpticalDrive drive,
        string isoPath,
        RipOptions options,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task EjectAsync(OpticalDrive drive, CancellationToken cancellationToken = default);

    /// <summary>Slot-loading drives cannot do this; check <see cref="OpticalDrive.CanLoadMedia"/>.</summary>
    Task CloseTrayAsync(OpticalDrive drive, CancellationToken cancellationToken = default);
}
