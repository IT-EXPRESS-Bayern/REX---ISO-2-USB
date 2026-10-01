// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Planning;

internal static class SchemeResolver
{
    /// <summary>
    /// MBR wherever BIOS matters or the device is small enough, GPT for UEFI-only media and for
    /// devices MBR cannot address. An explicit choice is kept unless the media cannot work with it.
    /// </summary>
    public static PartitionScheme Resolve(PlanContext ctx, TargetFirmware firmware, bool legacy)
    {
        var requested = ctx.Target.Scheme;

        if (ctx.Purpose == MediaPurpose.Dos)
        {
            if (requested == PartitionScheme.Gpt)
            {
                ctx.Warn(PlanWarningCodes.DosNeedsMbr);
            }

            return PartitionScheme.Mbr;
        }

        if (legacy)
        {
            if (requested == PartitionScheme.Gpt)
            {
                ctx.Warn(PlanWarningCodes.LegacyNeedsMbr);
            }

            return PartitionScheme.Mbr;
        }

        if (requested != PartitionScheme.Auto)
        {
            return requested;
        }

        if (ctx.DeviceBytes > uint.MaxValue * (long)ctx.SectorSize)
        {
            ctx.Warn(PlanWarningCodes.LargeDriveGpt, SizeText.Format(ctx.DeviceBytes));
            return PartitionScheme.Gpt;
        }

        return firmware == TargetFirmware.Uefi ? PartitionScheme.Gpt : PartitionScheme.Mbr;
    }
}
