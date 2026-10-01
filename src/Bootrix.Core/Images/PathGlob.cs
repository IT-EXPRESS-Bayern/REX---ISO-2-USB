// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Images;

/// <summary>
/// Glob pattern for image paths ("efi/**/*.efi"): <c>*</c> and <c>?</c> stay inside one segment, <c>**</c> spans any
/// number of segments, <c>**/</c> also matches no directory at all. Large Linux ISOs hold tens of thousands of paths
/// and the fingerprints ask dozens of questions about them, so literal patterns are answered by lookup and the
/// others are narrowed by their literal start and end before the regular expression runs.
/// </summary>
internal sealed class PathGlob
{
    private static readonly ConcurrentDictionary<string, PathGlob> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _prefix;
    private readonly string _suffix;
    private readonly Regex? _regex;

    private PathGlob(string pattern)
    {
        var firstWildcard = pattern.AsSpan().IndexOfAny('*', '?');
        if (firstWildcard < 0)
        {
            Exact = ImageFileIndex.Normalize(pattern);
            _prefix = _suffix = string.Empty;
            return;
        }

        var lastWildcard = pattern.AsSpan().LastIndexOfAny('*', '?');
        _prefix = pattern[..firstWildcard];

        // "**/name" also matches a plain "name", so the separator is not part of the required ending.
        _suffix = pattern[(lastWildcard + 1)..].TrimStart('/');
        _regex = new Regex(ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The normalised path for a pattern without wildcards.</summary>
    public string? Exact { get; }

    public static PathGlob Get(string pattern) => Cache.GetOrAdd(pattern, static p => new PathGlob(p));

    public bool IsMatch(string path)
    {
        if (Exact is not null)
        {
            return string.Equals(path, Exact, StringComparison.OrdinalIgnoreCase);
        }

        return path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
            && _regex!.IsMatch(path);
    }

    private static string ToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    pattern.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    pattern.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        return pattern.Append('$').ToString();
    }
}
