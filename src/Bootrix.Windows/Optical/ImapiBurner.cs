// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Imapi;
using Windows.Win32.System.Com;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Writes one IStream to the disc in a recorder with IDiscFormat2Data. IMAPI numbers the work of a burn as
/// actions (calibrating, writing, finalizing, verifying) and reports them through DDiscFormat2DataEvents,
/// about twice a second while data goes out; those reports become <see cref="BurnProgress"/>.
/// </summary>
internal sealed unsafe class ImapiBurner(ILogger logger)
{
    /// <summary>The name IMAPI shows other programs when a recorder is locked. Limited to letters, digits and a few separators.</summary>
    public const string ClientName = "Bootrix";

    private delegate void WriteUpdateHandler(object sender, object progress);

    public void Burn(
        ComScope com,
        IDiscRecorder2 recorder,
        OpticalDrive drive,
        IStream stream,
        long sectors,
        BurnOptions options,
        IProgress<BurnProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new ImapiErrorContext { DriveName = drive.DisplayName, NeededBytes = SectorMath.ToBytes(sectors), Cancellation = cancellationToken };

        var format = com.Add((IDiscFormat2Data)new MsftDiscFormat2Data());
        try
        {
            Prepare(format, recorder, drive, options);

            // Taking the lock ourselves lets us name the program that holds it; Write would only fail with the bare error code.
            AcquireExclusive(recorder, context);
            try
            {
                DisableAutoPlay(recorder);
                try
                {
                    using var events = new ComEvents(format, typeof(DDiscFormat2DataEvents).GUID);
                    events.TryOn(
                        (int)PInvoke.DISPID_DDISCFORMAT2DATAEVENTS_UPDATE,
                        new WriteUpdateHandler((_, args) => OnUpdate(args, format, progress, cancellationToken)),
                        logger);

                    using var cancel = cancellationToken.Register(() => TryCancel(format));
                    cancellationToken.ThrowIfCancellationRequested();

                    format.Write(stream);
                }
                finally
                {
                    EnableAutoPlay(recorder);
                }
            }
            finally
            {
                recorder.ReleaseExclusiveAccess();
            }
        }
        catch (Exception ex) when (ex is not (BootrixException or OperationCanceledException))
        {
            throw ImapiErrors.Translate(ex, context with { AvailableBytes = TryFreeBytes(format) });
        }
    }

    private void Prepare(IDiscFormat2Data format, IDiscRecorder2 recorder, OpticalDrive drive, BurnOptions options)
    {
        VARIANT_BOOL supported;
        format.IsRecorderSupported(recorder, &supported);
        if (!supported)
        {
            throw new BootrixException(ErrorCode.NoRecorder, drive.DisplayName);
        }

        // The recorder has to be set before anything that depends on the disc, and the client name before Write.
        format.Recorder = recorder;
        using (var name = Bstr.Allocate(ClientName))
        {
            format.ClientName = name.Value;
        }

        format.IsCurrentMediaSupported(recorder, &supported);
        if (!supported)
        {
            throw new BootrixException(ErrorCode.MediaNotSupported, drive.DisplayName);
        }

        format.ForceMediaToBeClosed = options.Finalize;
        format.ForceOverwrite = options.ForceOverwrite;
        if (!options.BufferUnderrunProtection)
        {
            DisableBufferUnderrunProtection(format);
        }

        SetVerification(format, options.Verify);

        // The speed is a number of sectors per second and depends on the disc type; the drive may pick a neighbouring speed, which IMAPI reports as a success code.
        var family = ((OpticalMediaType)(int)format.CurrentPhysicalMediaType).Family();
        format.SetWriteSpeed(SectorMath.ToSectorsPerSecond(family, options.WriteSpeedFactor), false);
    }

    private void DisableBufferUnderrunProtection(IDiscFormat2Data format)
    {
        try
        {
            format.BufferUnderrunFreeDisabled = true;
        }
        catch (COMException ex)
        {
            // IMAPI allows this for CD only; on other media protection simply stays on.
            logger.LogInformation(ex, "Buffer underrun protection cannot be switched off for this disc");
        }
    }

    private void SetVerification(IDiscFormat2Data format, BurnVerifyLevel level)
    {
        if (level == BurnVerifyLevel.None)
        {
            return;
        }

        try
        {
            var verification = (IBurnVerification)(object)format;
            verification.BurnVerificationLevel = (IMAPI_BURN_VERIFICATION_LEVEL)(int)level;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            logger.LogWarning(ex, "The drive's own verification is not available");
        }
    }

    private static void AcquireExclusive(IDiscRecorder2 recorder, ImapiErrorContext context)
    {
        try
        {
            using var name = Bstr.Allocate(ClientName);
            recorder.AcquireExclusiveAccess(false, name.Value);
        }
        catch (COMException ex) when (ex.HResult == HRESULT.E_IMAPI_RECORDER_LOCKED.Value)
        {
            string? owner = null;
            try
            {
                owner = Bstr.Take(recorder.ExclusiveAccessOwner);
            }
            catch (COMException)
            {
                // The owner is a courtesy to the user; without it the message still says that the drive is busy.
            }

            throw ImapiErrors.Translate(ex, context with { LockOwner = owner });
        }
    }

    private void DisableAutoPlay(IDiscRecorder2 recorder)
    {
        try
        {
            recorder.DisableMcn();
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "Media change notification could not be disabled");
        }
    }

    private void EnableAutoPlay(IDiscRecorder2 recorder)
    {
        try
        {
            recorder.EnableMcn();
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "Media change notification could not be enabled again");
        }
    }

    private void OnUpdate(object args, IDiscFormat2Data format, IProgress<BurnProgress>? progress, CancellationToken cancellationToken)
    {
        // IMAPI calls this on its own thread and waits for the return; an exception here would be thrown into the burn.
        try
        {
            var e = (IDiscFormat2DataEventArgs)args;
            progress?.Report(BurnProgress.FromWriteEvent(
                (int)e.CurrentAction,
                e.StartLba,
                e.SectorCount,
                e.LastWrittenLba,
                e.ElapsedTime,
                e.RemainingTime,
                e.TotalSystemBuffer,
                e.UsedSystemBuffer));

            // The cancel request may arrive just before Write starts, when there is nothing to cancel yet; asking again here covers that gap.
            if (cancellationToken.IsCancellationRequested)
            {
                TryCancel(format);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Progress notification could not be processed");
        }
        finally
        {
            if (Marshal.IsComObject(args))
            {
                Marshal.FinalReleaseComObject(args);
            }
        }
    }

    private static void TryCancel(IDiscFormat2Data format)
    {
        try
        {
            format.CancelWrite();
        }
        catch (COMException)
        {
            // No write in progress: it has finished or not started, and either way there is nothing left to cancel.
        }
    }

    private static long? TryFreeBytes(IDiscFormat2Data format)
    {
        try
        {
            return SectorMath.ToBytes(format.FreeSectorsOnMedia);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
