// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Writing.Linux.Patching;

/// <summary>
/// Replaces the label of the ISO volume with the label of the medium in the places where boot configurations name it:
/// <c>root=live:CDLABEL=…</c>, <c>inst.stage2=hd:LABEL=…</c>, <c>archisolabel=…</c>, <c>search --label …</c>.
/// Only text that follows such a key is changed, so a label like "Debian" does not touch a host name.
/// Spaces and slashes in labels are written as <c>\x20</c> and <c>\x2f</c> by udev and dracut; both spellings are handled.
/// </summary>
internal sealed class LabelRewriter
{
    // What may precede a label: a "...label=" key, the --label / -l options of GRUB's search, or udev's by-label directory.
    // The key is matched case-insensitively (CDLABEL=, archisolabel=); the label itself is compared exactly.
    private const string Context = @"(?<=(?i:label=|--label[ \t]+|-l[ \t]+|by-label/)[""']?)";
    private const string Boundary = @"(?![A-Za-z0-9_.\-])";

    private readonly Regex _pattern;
    private readonly Dictionary<string, string> _replacements;

    private LabelRewriter(Regex pattern, Dictionary<string, string> replacements)
    {
        _pattern = pattern;
        _replacements = replacements;
    }

    /// <summary>Null when there is nothing to rewrite: no labels, or the same one on both sides.</summary>
    public static LabelRewriter? Create(string? oldLabel, string? newLabel)
    {
        if (string.IsNullOrEmpty(oldLabel) || string.IsNullOrEmpty(newLabel) || string.Equals(oldLabel, newLabel, StringComparison.Ordinal))
        {
            return null;
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawReplacement = ContainsBlank(oldLabel) || !ContainsBlank(newLabel) ? newLabel : EscapeBlanks(newLabel);
        foreach (var (variant, replacement) in new[]
        {
            (oldLabel, rawReplacement),
            (EscapeBlanks(oldLabel), EscapeBlanks(newLabel)),
            (EscapeSlashes(EscapeBlanks(oldLabel)), EscapeSlashes(EscapeBlanks(newLabel))),
        })
        {
            replacements.TryAdd(variant, replacement);
        }

        var alternatives = string.Join('|', replacements.Keys.OrderByDescending(v => v.Length).Select(Regex.Escape));
        var pattern = new Regex(Context + "(?:" + alternatives + ")" + Boundary, RegexOptions.CultureInvariant);
        return new LabelRewriter(pattern, replacements);
    }

    /// <summary>The line with every label replaced, or the very same string when it names none.</summary>
    public string Rewrite(string line)
    {
        var rewritten = _pattern.Replace(line, match => _replacements.GetValueOrDefault(match.Value, match.Value));
        return string.Equals(rewritten, line, StringComparison.Ordinal) ? line : rewritten;
    }

    private static bool ContainsBlank(string value) => value.Contains(' ') || value.Contains('\t');

    private static string EscapeBlanks(string value) => value.Replace(" ", @"\x20", StringComparison.Ordinal).Replace("\t", @"\x09", StringComparison.Ordinal);

    private static string EscapeSlashes(string value) => value.Replace("/", @"\x2f", StringComparison.Ordinal);
}
