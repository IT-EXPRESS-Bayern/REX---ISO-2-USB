// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Optical;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Imapi;

namespace Bootrix.Windows.Optical;

/// <summary>Finds the recorders IMAPI knows and reads their identity and what they can write.</summary>
internal static unsafe class ImapiDrives
{
    public static IReadOnlyList<OpticalDrive> Enumerate(ILogger logger)
    {
        using var com = new ComScope();
        var master = com.Add((IDiscMaster2)new MsftDiscMaster2());

        // False when there is no optical drive at all, but also when policy forbids burning; either way there is nothing to list.
        if (!master.IsSupportedEnvironment)
        {
            logger.LogInformation("IMAPI reports no usable optical devices in this environment");
            return [];
        }

        var drives = new List<OpticalDrive>();
        var count = master.Count;
        for (var i = 0; i < count; i++)
        {
            BSTR raw;
            master.get_Item(i, &raw);
            var id = Bstr.Take(raw);
            try
            {
                drives.Add(Describe(com, id));
            }
            catch (COMException ex)
            {
                // One drive that answers badly (a USB drive being unplugged) must not hide the others.
                logger.LogWarning(ex, "Recorder {Id} could not be read", id);
            }
        }

        return drives;
    }

    public static IDiscRecorder2 CreateRecorder(ComScope com, string uniqueId)
    {
        var recorder = com.Add((IDiscRecorder2)new MsftDiscRecorder2());
        using var id = Bstr.Allocate(uniqueId);
        recorder.InitializeDiscRecorder(id.Value);
        return recorder;
    }

    private static OpticalDrive Describe(ComScope com, string uniqueId)
    {
        var recorder = CreateRecorder(com, uniqueId);

        var capabilities = OpticalCapabilities.None;
        try
        {
            capabilities = OpticalCapabilitiesExtensions.FromMmcProfiles(SafeArrays.ReadInts(recorder.SupportedProfiles));
        }
        catch (COMException)
        {
            // Old drives cannot be asked for their feature list; they are listed as readers.
        }

        var letter = SafeArrays.ReadStrings(recorder.VolumePathNames)
            .FirstOrDefault(path => path.Length >= 2 && path[1] == ':');

        return new OpticalDrive
        {
            Id = uniqueId,
            Vendor = Bstr.Take(recorder.VendorId).Trim(),
            Product = Bstr.Take(recorder.ProductId).Trim(),
            Revision = Bstr.Take(recorder.ProductRevision).Trim(),
            DriveLetter = letter?[..2],
            DeviceNumber = recorder.LegacyDeviceNumber,
            Capabilities = capabilities,
            CanLoadMedia = recorder.DeviceCanLoadMedia,
        };
    }
}
