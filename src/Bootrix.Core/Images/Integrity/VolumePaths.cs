// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Integrity;

/// <summary>
/// String handling for volume roots and image paths as the platform layer reports them. Win32, NT and
/// device spellings of the same location have to compare equal ("C:\", "\\?\C:\", "c:").
/// </summary>
internal static class VolumePaths
{
    /// <summary>
    /// Upper case, backslashes, no trailing separator, no "\\?\" or "\??\" prefix, and "\\?\UNC\server\share"
    /// as "\\server\share".
    /// </summary>
    public static string Normalize(string path)
    {
        var text = path.Trim().Replace('/', '\\');
        foreach (var prefix in new[] { @"\\?\", @"\\.\", @"\??\" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        if (text.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            text = @"\\" + text[4..];
        }

        return text.TrimEnd('\\').ToUpperInvariant();
    }

    /// <summary>The path is the root itself or lies below it; both in normalised form.</summary>
    public static bool IsUnder(string root, string path) =>
        path.Equals(root, StringComparison.Ordinal) || path.StartsWith(root + "\\", StringComparison.Ordinal);

    /// <summary>
    /// True when the longest root that contains <paramref name="imagePath"/> is one of <paramref name="targetRoots"/>.
    /// Mount folders may nest, so a plain prefix test against the target roots would misattribute an image that
    /// lives on a volume mounted below the target's drive letter.
    /// </summary>
    public static bool IsOwnedBy(string imagePath, IEnumerable<string> targetRoots, IEnumerable<string> otherRoots)
    {
        var image = Normalize(imagePath);
        var targets = targetRoots.Select(Normalize).Where(root => root.Length > 0).ToHashSet(StringComparer.Ordinal);
        var all = targets.Concat(otherRoots.Select(Normalize).Where(root => root.Length > 0));

        var owner = all.Where(root => IsUnder(root, image)).OrderByDescending(root => root.Length).FirstOrDefault();
        return owner is not null && targets.Contains(owner);
    }
}
