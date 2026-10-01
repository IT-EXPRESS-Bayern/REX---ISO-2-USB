// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Imapi;

namespace Bootrix.Windows.Optical;

/// <summary>Erases rewritable discs with IDiscFormat2Erase.</summary>
internal sealed unsafe class ImapiEraser(ILogger logger)
{
    // DISPID_DDISCFORMAT2ERASEEVENTS_UPDATE in imapi2.h; the metadata the bindings come from does not carry it.
    private const int UpdateDispatchId = 0x200;

    private delegate void EraseUpdateHandler(object sender, int elapsedSeconds, int estimatedTotalSeconds);

    /// <summary>
    /// IMAPI has no call to stop an erase once the drive has it. The token is therefore only looked at
    /// before the erase starts; a user who cancels later has to wait for it to finish.
    /// </summary>
    public void Erase(ComScope com, IDiscRecorder2 recorder, OpticalDrive drive, EraseMode mode, IProgress<EraseProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new ImapiErrorContext { DriveName = drive.DisplayName, Cancellation = cancellationToken };
        try
        {
            var erase = com.Add((IDiscFormat2Erase)new MsftDiscFormat2Erase());

            VARIANT_BOOL supported;
            erase.IsRecorderSupported(recorder, &supported);
            if (!supported)
            {
                throw new BootrixException(ErrorCode.NoRecorder, drive.DisplayName);
            }

            erase.Recorder = recorder;
            using (var name = Bstr.Allocate(ImapiBurner.ClientName))
            {
                erase.ClientName = name.Value;
            }

            erase.IsCurrentMediaSupported(recorder, &supported);
            if (!supported)
            {
                throw new BootrixException(ErrorCode.MediaNotSupported, drive.DisplayName);
            }

            // A quick erase clears only the directory area, which is all that is needed to write the disc again.
            erase.FullErase = mode == EraseMode.Full;

            using var events = new ComEvents(erase, typeof(DDiscFormat2EraseEvents).GUID);
            events.TryOn(UpdateDispatchId, new EraseUpdateHandler((_, elapsed, total) => OnUpdate(progress, elapsed, total)), logger);

            cancellationToken.ThrowIfCancellationRequested();
            erase.EraseMedia();
            progress?.Report(new EraseProgress(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        }
        catch (Exception ex) when (ex is not (BootrixException or OperationCanceledException))
        {
            throw ImapiErrors.Translate(ex, context);
        }
    }

    private void OnUpdate(IProgress<EraseProgress>? progress, int elapsedSeconds, int estimatedTotalSeconds)
    {
        try
        {
            progress?.Report(new EraseProgress(TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds)), TimeSpan.FromSeconds(Math.Max(0, estimatedTotalSeconds))));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Erase progress could not be processed");
        }
    }
}
