// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;

namespace Bootrix.Core.Planning;

internal static class WriteMethodResolver
{
    public static WriteMethod Resolve(PlanContext ctx)
    {
        var image = ctx.Image;
        var mode = ctx.Target.Mode;

        if (ctx.Target.WindowsToGo)
        {
            return image.Kind == ImageKind.WindowsSetup && mode != WriteMode.RawCopy
                ? WriteMethod.ApplyImage
                : throw PlanContext.Unsupported(ErrorCode.WriteModeUnsupported, "WindowsToGo", image.Kind);
        }

        var rawOnly = image.Kind is ImageKind.RawDisk or ImageKind.Bsd or ImageKind.Apple;
        var canCopyRaw = rawOnly || image.IsHybrid || image.Kind == ImageKind.LinuxHybrid;

        switch (mode)
        {
            case WriteMode.RawCopy:
                return canCopyRaw ? WriteMethod.RawCopy : throw PlanContext.Unsupported(ErrorCode.WriteModeUnsupported, mode, image.Kind);
            case WriteMode.Extract:
                return rawOnly ? throw PlanContext.Unsupported(ErrorCode.WriteModeUnsupported, mode, image.Kind) : FilesMethod(image.Kind);
            default:
                // Hybrid Linux images are written raw by default: upstream says so, and it is the most reliable path.
                var raw = rawOnly || image.Kind == ImageKind.LinuxHybrid || (image.Kind == ImageKind.Unknown && image.IsHybrid);
                return raw ? WriteMethod.RawCopy : FilesMethod(image.Kind);
        }
    }

    /// <summary>FreeDOS media and plain formatting have no image whose files could be copied.</summary>
    private static WriteMethod FilesMethod(ImageKind kind) =>
        kind is ImageKind.Dos or ImageKind.Unknown ? WriteMethod.FormatOnly : WriteMethod.ExtractFiles;
}
