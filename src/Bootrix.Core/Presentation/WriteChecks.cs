// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;
using Bootrix.Core.Storage;
using Bootrix.Core.Text;

namespace Bootrix.Core.Presentation;

public static class WriteChecks
{
    /// <summary>The first thing that keeps a write from starting, as a ready-made message; null when nothing does.</summary>
    public static string? FirstProblem(string? imagePath, long? imageBytes, IReadOnlyList<StorageDevice> targets, Localizer localizer)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return localizer.Get("Write.NoImage");
        }

        if (targets.Count == 0)
        {
            return localizer.Get("Write.NoTarget");
        }

        if (imageBytes is { } size)
        {
            var tooSmall = targets.FirstOrDefault(t => t.SizeBytes < size);
            if (tooSmall is not null)
            {
                return localizer.Get("Write.TooSmall", ByteSize.Format(size, localizer.Culture), ByteSize.Format(tooSmall.SizeBytes, localizer.Culture));
            }
        }

        return null;
    }
}
