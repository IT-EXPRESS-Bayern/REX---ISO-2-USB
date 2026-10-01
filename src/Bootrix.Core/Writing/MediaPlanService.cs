// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Images;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Raw;

namespace Bootrix.Core.Writing;

/// <summary>An image together with what Bootrix intends to do with it on one device. The GUI shows it before anything is written.</summary>
public sealed record WritePreview(ImageInspection Inspection, MediaPlan Plan);

/// <summary>Looks at an image and plans the medium. It touches no device and needs no administrator rights.</summary>
public sealed class MediaPlanService(ImageInspector inspector)
{
    public async Task<ImageInspection> InspectAsync(string imagePath, CancellationToken cancellationToken = default) =>
        (await InspectWithHintsAsync(imagePath, cancellationToken).ConfigureAwait(false)).Inspection;

    public Task<ImageInspection> InspectAsync(Stream image, string? fileName, CancellationToken cancellationToken = default) =>
        inspector.InspectAsync(image, fileName, cancellationToken: cancellationToken);

    public Task<ImageInspection> InspectAsync(Stream image, string? fileName, ImageInspectOptions? options, CancellationToken cancellationToken = default) =>
        inspector.InspectAsync(image, fileName, options, cancellationToken);

    public async Task<WritePreview> PlanAsync(
        string imagePath,
        TargetOptions target,
        StorageDevice device,
        CancellationToken cancellationToken = default)
    {
        var refined = await InspectWithHintsAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var preview = Plan(refined.Inspection, target, device);
        return preview with { Plan = refined.Apply(preview.Plan) };
    }

    /// <summary>
    /// Apple containers are inspected by the volume they hold, everything else by the file; a block map next to the image
    /// is reported either way.
    /// </summary>
    private async Task<AppleRefinement> InspectWithHintsAsync(string imagePath, CancellationToken cancellationToken)
    {
        using var source = await Task.Run(() => ImageSourceOpener.OpenForInspection(imagePath), cancellationToken).ConfigureAwait(false);
        var inspection = await inspector.InspectAsync(source.Stream, Path.GetFileName(imagePath), cancellationToken: cancellationToken).ConfigureAwait(false);
        inspection = inspection with { BmapPath = BlockMapLocator.Find(imagePath) };
        return AppleImagePlanning.Refine(inspection, source.Stream, source.Kind == ImageSourceKind.AppleContainer);
    }

    /// <summary>What stands in for the image when nothing is copied from one: a DOS system or an empty, formatted drive.</summary>
    public static ImageInspection InspectionFor(WriteSource source) => new()
    {
        Profile = new ImageProfile { Kind = source == WriteSource.Dos ? ImageKind.Dos : ImageKind.Unknown },
        Container = ImageContainer.Unknown,
    };

    public static WritePreview Plan(ImageInspection inspection, TargetOptions target, StorageDevice device) =>
        new(inspection, LayoutPlanner.Plan(inspection.Profile, target, CapsOf(device)));

    public static DeviceCaps CapsOf(StorageDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var bus = device.Bus switch
        {
            BusType.Usb => DeviceBus.Usb,
            BusType.Sata => DeviceBus.Sata,
            BusType.Ata => DeviceBus.Ata,
            BusType.Nvme => DeviceBus.Nvme,
            BusType.Sd => DeviceBus.Sd,
            BusType.Mmc => DeviceBus.Mmc,
            BusType.Scsi or BusType.Sas or BusType.RAID or BusType.IScsi => DeviceBus.Scsi,
            BusType.Ieee1394 => DeviceBus.Ieee1394,
            BusType.Virtual or BusType.FileBackedVirtual => DeviceBus.Virtual,
            _ => DeviceBus.Unknown,
        };

        var medium = device switch
        {
            { IsFloppy: true } => DeviceMedium.Floppy,
            { Bus: BusType.Sd or BusType.Mmc } => DeviceMedium.Card,
            { IsRemovableMedia: false } => DeviceMedium.Hdd,
            _ => DeviceMedium.Stick,
        };

        return new DeviceCaps
        {
            SizeBytes = device.SizeBytes,
            LogicalSectorSize = device.LogicalSectorSize,
            PhysicalSectorSize = device.PhysicalSectorSize,
            Removable = device.IsRemovableMedia,
            Bus = bus,
            Medium = medium,
        };
    }
}
