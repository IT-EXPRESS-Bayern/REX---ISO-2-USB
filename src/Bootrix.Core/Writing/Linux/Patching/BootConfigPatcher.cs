// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Writing.Linux.Patching;

/// <summary>
/// Adapts the boot configuration files of an image (Syslinux, GRUB and systemd-boot entries) to the medium it is
/// copied to: the volume label that live systems search for, and the kernel parameter that switches on persistence.
/// A pure function of text and options. Only lines that start a boot command line are touched, and only where a
/// known pattern matches; everything else, including line endings, tabs and unknown directives, is returned unchanged.
/// </summary>
public static partial class BootConfigPatcher
{
    // Directives whose lines carry a kernel command line: Syslinux "append", systemd-boot "options", GRUB's linux family.
    private static readonly HashSet<string> CommandLineDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "append", "options", "linux", "linux16", "linuxefi", "linux32", "$linux",
    };

    // Lines in which a volume label may appear: the above plus the places Rufus looks (kernel, search, for).
    private static readonly HashSet<string> LabelDirectives = new(CommandLineDirectives, StringComparer.OrdinalIgnoreCase)
    {
        "kernel", "search", "for",
    };

    private static readonly HashSet<string> GrubKernelDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "linux", "linux16", "linuxefi", "linux32", "$linux",
    };

    /// <summary>
    /// Patches the text of one configuration file. The text is expected to be decoded so that every byte is one char
    /// (Latin-1), which keeps files with unknown encodings byte-exact; labels are compared the same way.
    /// </summary>
    public static ConfigPatchResult Patch(string text, ConfigPatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        var label = LabelRewriter.Create(options.OldLabel, options.NewLabel);
        var changes = new List<ConfigChange>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith('\r') ? "\r" : "";
            var body = ending.Length == 0 ? line : line[..^1];

            var patched = PatchLine(body, i + 1, options, label, changes);
            if (!ReferenceEquals(patched, body))
            {
                lines[i] = patched + ending;
            }
        }

        return new ConfigPatchResult(changes.Count == 0 ? text : string.Join('\n', lines), changes);
    }

    private static string PatchLine(string line, int number, ConfigPatchOptions options, LabelRewriter? label, List<ConfigChange> changes)
    {
        var match = Directive().Match(line);
        if (!match.Success)
        {
            return line;
        }

        var directive = match.Groups["directive"].Value;
        var current = line;
        if (label is not null && LabelDirectives.Contains(directive))
        {
            var rewritten = label.Rewrite(current);
            if (!ReferenceEquals(rewritten, current))
            {
                changes.Add(new ConfigChange(number, ConfigChangeKind.Label, current, rewritten));
                current = rewritten;
            }
        }

        if (!CommandLineDirectives.Contains(directive))
        {
            return current;
        }

        var isGrub = GrubKernelDirectives.Contains(directive);
        current = ApplyPersistence(current, number, options.Persistence, isGrub, changes);
        if (options.EsxiFirstPartition && directive.Equals("append", StringComparison.OrdinalIgnoreCase))
        {
            current = ApplyEsxiPartition(current, number, changes);
        }

        return current;
    }

    private static string ApplyPersistence(string line, int number, PersistenceStyle style, bool isGrub, List<ConfigChange> changes)
    {
        if (style == PersistenceStyle.None)
        {
            return line;
        }

        var tokens = Tokens(line);
        var keyword = style == PersistenceStyle.Casper ? "persistent" : "persistence";
        var current = line;

        // casper starts the installer instead of the live session on "maybe-ubiquity", which would ignore the store.
        if (style == PersistenceStyle.Casper)
        {
            var maybe = tokens.FirstOrDefault(t => t.Index > 0 && t.Value.Equals("maybe-ubiquity", StringComparison.Ordinal));
            if (maybe is not null)
            {
                current = Remove(current, maybe);
                changes.Add(new ConfigChange(number, ConfigChangeKind.RemovedParameter, line, current));
                tokens = Tokens(current);
            }
        }

        if (tokens.Any(t => t.Index > 0 && t.Value.Equals(keyword, StringComparison.Ordinal)))
        {
            return current;
        }

        var anchor = FindAnchor(tokens, style, isGrub);
        if (anchor is null)
        {
            return current;
        }

        var inserted = anchor.Value.After
            ? InsertAfter(current, anchor.Value.Token, keyword)
            : InsertBefore(current, anchor.Value.Token, keyword);
        changes.Add(new ConfigChange(number, ConfigChangeKind.PersistenceParameter, current, inserted));
        return inserted;
    }

    /// <summary>
    /// Where the persistence keyword belongs: after the parameter that names the live system, or for casper before the
    /// preseed file, or after the kernel path of a GRUB line. In that order of preference; null when the line is none of these.
    /// </summary>
    private static (Token Token, bool After)? FindAnchor(List<Token> tokens, PersistenceStyle style, bool isGrub)
    {
        var arguments = tokens.Where(t => t.Index > 0).ToList();
        if (style == PersistenceStyle.LiveBoot)
        {
            var live = arguments.FirstOrDefault(t => t.Value.Equals("boot=live", StringComparison.Ordinal));
            return live is null ? null : (live, true);
        }

        var casper = arguments.FirstOrDefault(t => t.Value.Equals("boot=casper", StringComparison.Ordinal));
        if (casper is not null)
        {
            return (casper, true);
        }

        var preseed = arguments.FirstOrDefault(t => t.Value.StartsWith("file=/cdrom/preseed", StringComparison.Ordinal));
        if (preseed is not null)
        {
            return (preseed, false);
        }

        var kernel = isGrub ? arguments.FirstOrDefault() : null;
        return kernel is not null && CasperKernel().IsMatch(kernel.Value) ? (kernel, true) : null;
    }

    private static string ApplyEsxiPartition(string line, int number, List<ConfigChange> changes)
    {
        var tokens = Tokens(line);
        if (tokens.Any(t => t.Value.Equals("-p", StringComparison.Ordinal)))
        {
            return line;
        }

        var config = tokens.FirstOrDefault(t => t.Index > 0 && t.Value.Equals("boot.cfg", StringComparison.OrdinalIgnoreCase)
            && tokens[t.Index - 1].Value.Equals("-c", StringComparison.Ordinal));
        if (config is null)
        {
            return line;
        }

        var patched = line[..(config.Start + config.Value.Length)] + " -p 1" + line[(config.Start + config.Value.Length)..];
        changes.Add(new ConfigChange(number, ConfigChangeKind.EsxiPartition, line, patched));
        return patched;
    }

    private static string InsertAfter(string line, Token token, string value) =>
        line[..(token.Start + token.Value.Length)] + " " + value + line[(token.Start + token.Value.Length)..];

    private static string InsertBefore(string line, Token token, string value) =>
        line[..token.Start] + value + " " + line[token.Start..];

    private static string Remove(string line, Token token)
    {
        // Take the blank in front of the token with it, so no double blank is left behind.
        var start = token.Start;
        while (start > 0 && line[start - 1] is ' ' or '\t')
        {
            start--;
        }

        return line[..start] + line[(token.Start + token.Value.Length)..];
    }

    private static List<Token> Tokens(string line) =>
        [.. Word().Matches(line).Select((m, index) => new Token(index, m.Index, m.Value))];

    private sealed record Token(int Index, int Start, string Value);

    [GeneratedRegex(@"^[ \t]*(?<directive>\S+)(?=[ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"/casper[^/ ]*/vmlinuz", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CasperKernel();
}
