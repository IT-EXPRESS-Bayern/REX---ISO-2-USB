// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;
using Windows.Win32.Storage.Imapi;
using Windows.Win32.System.Com;

namespace Bootrix.Windows.Optical;

/// <summary>The result image of an IFileSystemImage and the boot image streams it still reads from.</summary>
internal sealed class ImapiFolderImage(IStream stream, long sectors, List<ImapiStream> bootImages) : IDisposable
{
    public IStream Stream { get; } = stream;

    public long Sectors { get; } = sectors;

    public void Dispose()
    {
        foreach (var image in bootImages)
        {
            image.Dispose();
        }
    }
}

/// <summary>
/// Builds the image of a data disc from a folder with IMAPI2FS. The file system layers and the UDF revision come
/// from <see cref="FolderBurnPlanner"/>; boot entries become El Torito entries, several of them (BIOS and UEFI)
/// through IFileSystemImage2, which exists since Windows Vista SP1.
/// </summary>
internal static unsafe class ImapiFolderBuilder
{
    public static ImapiFolderImage Build(
        ComScope com,
        IDiscRecorder2 recorder,
        FolderBurnRequest request,
        DiscFileSystems fileSystems,
        string volumeLabel)
    {
        var image = com.Add((IFileSystemImage2)new MsftFileSystemImage());

        // Defaults first: they depend on the disc in the drive and everything set afterwards overrides them.
        image.ChooseImageDefaults(recorder);
        image.FileSystemsToCreate = (FsiFileSystems)(int)fileSystems;
        if (fileSystems.HasFlag(DiscFileSystems.Udf))
        {
            image.UDFRevision = (int)request.UdfRevision;
        }

        // The default limit is the size of a 650 MB CD; this takes the free space of the disc in the drive instead.
        image.SetMaxMediaBlocksFromDevice(recorder);
        using (var label = Bstr.Allocate(volumeLabel))
        {
            image.VolumeName = label.Value;
        }

        var bootImages = new List<ImapiStream>();
        try
        {
            AssignBootEntries(com, image, request.BootEntries, bootImages);

            using (var source = Bstr.Allocate(request.SourceFolder))
            {
                var root = com.Add(image.Root);
                root.AddTree(source.Value, request.IncludeBaseDirectory);
            }

            image.CreateResultImage(out var result);
            com.Add(result);
            var stream = com.Add(result.ImageStream);
            return new ImapiFolderImage(stream, (long)result.TotalBlocks * result.BlockSize / SectorMath.SectorSize, bootImages);
        }
        catch
        {
            foreach (var boot in bootImages)
            {
                boot.Dispose();
            }

            throw;
        }
    }

    private static void AssignBootEntries(ComScope com, IFileSystemImage2 image, IReadOnlyList<DiscBootEntry> entries, List<ImapiStream> streams)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var options = new List<object>();
        foreach (var entry in entries)
        {
            var boot = com.Add((IBootOptions)new BootOptions());
            var file = ImapiStream.FromFile(entry.ImagePath);
            streams.Add(file);

            boot.AssignBootImage(file.Stream);
            boot.PlatformId = (PlatformId)(int)entry.Platform;
            boot.Emulation = (EmulationType)(int)entry.Emulation;
            if (entry.Manufacturer is { } manufacturer)
            {
                using var text = Bstr.Allocate(manufacturer);
                boot.Manufacturer = text.Value;
            }

            options.Add(boot);
        }

        if (options.Count == 1)
        {
            image.BootImageOptions = (IBootOptions)options[0];
            return;
        }

        // The order of the array is the order of the entries on the disc; the first is what a BIOS boots, UEFI looks for its own platform ID.
        var array = SafeArrays.CreateObjectVector(options);
        try
        {
            image.BootImageOptionsArray = array;
        }
        finally
        {
            SafeArrays.Destroy(array);
        }
    }
}
