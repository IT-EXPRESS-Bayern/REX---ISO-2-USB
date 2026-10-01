// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Planning;

/// <summary>
/// Decides how a device is laid out for an image: write method, partition scheme, partitions,
/// file systems and boot method. A pure function of its three inputs; it touches no device and
/// every limit it enforces is a property of the formats, not of the machine it runs on.
/// </summary>
public static class LayoutPlanner
{
    private const int MaxClusterBytes = 32 * 1024 * 1024;

    /// <exception cref="BootrixException">The device is too small, or the request cannot be satisfied for this image and device.</exception>
    public static MediaPlan Plan(ImageProfile image, TargetOptions target, DeviceCaps device)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(device);

        var context = new PlanContext(image, target, device);
        Validate(context);
        AddDeviceWarnings(context);

        var method = WriteMethodResolver.Resolve(context);
        return method == WriteMethod.RawCopy ? RawCopyPlan.Create(context) : FilePlan.Create(context, method);
    }

    private static void Validate(PlanContext ctx)
    {
        if (ctx.Device.LogicalSectorSize is not (512 or 4096))
        {
            throw PlanContext.Unsupported(ErrorCode.SectorSizeUnsupported, ctx.Device.LogicalSectorSize, "Bootrix");
        }

        if (ctx.DeviceBytes <= 0)
        {
            throw ctx.DeviceTooSmall(PlanLimits.Mib);
        }

        if (ctx.Target.ClusterSizeBytes is { } cluster && (cluster < 512 || cluster > MaxClusterBytes || !int.IsPow2(cluster)))
        {
            var detail = $"cluster size {cluster} is not a power of two between 512 bytes and 32 MiB";
            throw new BootrixException(ErrorCode.InvalidSpec, detail) { Arguments = [detail] };
        }
    }

    private static void AddDeviceWarnings(PlanContext ctx)
    {
        var device = ctx.Device;
        if (!device.Removable)
        {
            ctx.Warn(PlanWarningCodes.FixedDisk);
        }

        if (device.LogicalSectorSize == 4096 && device.Bus == DeviceBus.Usb)
        {
            ctx.Warn(PlanWarningCodes.FourKnUsbBridge);
        }
    }
}
