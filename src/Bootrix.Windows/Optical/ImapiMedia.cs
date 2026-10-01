// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Optical;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Imapi;

namespace Bootrix.Windows.Optical;

/// <summary>Reads the state of the disc in a recorder through IDiscFormat2Data.</summary>
internal static unsafe class ImapiMedia
{
    /// <summary>
    /// Null when IMAPI cannot say anything about the recorder (a reader, not a writer); the caller then
    /// looks at the drive without IMAPI. An empty tray gives <see cref="OpticalMedia.None"/>.
    /// </summary>
    public static OpticalMedia? Query(ComScope com, IDiscRecorder2 recorder)
    {
        var format = com.Add((IDiscFormat2Data)new MsftDiscFormat2Data());

        VARIANT_BOOL recorderSupported;
        format.IsRecorderSupported(recorder, &recorderSupported);
        if (!recorderSupported)
        {
            return null;
        }

        format.Recorder = recorder;
        try
        {
            return Read(format, recorder);
        }
        catch (COMException ex) when (ex.HResult == HRESULT.E_IMAPI_RECORDER_MEDIA_NO_MEDIA.Value)
        {
            return OpticalMedia.None;
        }
    }

    private static OpticalMedia Read(IDiscFormat2Data format, IDiscRecorder2 recorder)
    {
        var type = (OpticalMediaType)(int)format.CurrentPhysicalMediaType;
        var state = (OpticalMediaState)(int)format.CurrentMediaStatus;

        VARIANT_BOOL supported;
        format.IsCurrentMediaSupported(recorder, &supported);

        return new OpticalMedia
        {
            Type = type,
            State = state,
            IsSupported = supported,
            HeuristicallyBlank = format.MediaHeuristicallyBlank,
            FreeSectors = format.FreeSectorsOnMedia,
            TotalSectors = format.TotalSectorsOnMedia,
            NextWritableAddress = format.NextWritableAddress,
            StartOfPreviousSession = format.StartAddressOfPreviousSession,
            LastWrittenOfPreviousSession = format.LastWrittenAddressOfPreviousSession,
            WriteSpeeds = ReadWriteSpeeds(format),
        };
    }

    private static List<WriteSpeed> ReadWriteSpeeds(IDiscFormat2Data format)
    {
        var speeds = new List<WriteSpeed>();
        foreach (var item in SafeArrays.Read(format.SupportedWriteSpeedDescriptors))
        {
            if (item is not IWriteSpeedDescriptor descriptor)
            {
                continue;
            }

            try
            {
                speeds.Add(new WriteSpeed(descriptor.WriteSpeed, descriptor.RotationTypeIsPureCAV));
            }
            finally
            {
                Marshal.FinalReleaseComObject(descriptor);
            }
        }

        return speeds;
    }
}
