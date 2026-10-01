// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Writing.Linux.Patching;

/// <summary>
/// The <c>md5sum.txt</c> / <c>MD5SUMS</c> list that Ubuntu, Debian and others keep in the root of their images, read by
/// "check disc for defects" menu entries. Files that were patched on the way to the medium no longer match their line.
/// </summary>
public static partial class Md5SumFile
{
    public static bool IsListName(string path) =>
        path.Equals("md5sum.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("MD5SUMS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the checksum of every listed file for which a new one is given. Paths are compared without a leading
    /// "./" and ignoring case; lines of other files, comments and line endings stay as they are.
    /// </summary>
    /// <param name="text">The list, decoded one char per byte.</param>
    /// <param name="checksums">New MD5 values (32 hex digits) by path relative to the root of the medium.</param>
    public static string Update(string text, IReadOnlyDictionary<string, string> checksums)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(checksums);
        if (checksums.Count == 0)
        {
            return text;
        }

        var wanted = checksums.ToDictionary(pair => Normalize(pair.Key), pair => pair.Value.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
        return Entry().Replace(text, match =>
        {
            var path = Normalize(match.Groups["path"].Value.TrimEnd('\r'));
            return wanted.TryGetValue(path, out var sum)
                ? sum + match.Groups["separator"].Value + match.Groups["path"].Value
                : match.Value;
        });
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.').TrimStart('/');

    // "<32 hex digits>  ./path" in text mode, "<32 hex digits> *./path" in binary mode.
    [GeneratedRegex(@"^(?<sum>[0-9A-Fa-f]{32})(?<separator>[ \t]+\*?)(?<path>[^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Entry();
}
