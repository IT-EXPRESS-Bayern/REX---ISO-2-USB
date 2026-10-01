// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Json;

namespace Bootrix.Core.Images.Families;

/// <summary>What the fingerprint rules get to look at.</summary>
internal sealed class FamilyContext
{
    public required bool IsOptical { get; init; }

    public string? Label { get; init; }

    public string? FileName { get; init; }

    public long ImageBytes { get; init; }

    /// <summary>The file trees to search: the image's own plus those of embedded boot images (floppy, EFI).</summary>
    public required IReadOnlyList<ImageFileIndex> Trees { get; init; }

    public required Func<string, string?> ReadText { get; init; }

    public DiskLayout? Layout { get; init; }
}

internal sealed record FamilyMatch(string Family, string Kind);

/// <summary>Evaluates the data-driven fingerprints of <c>family-rules.json</c>.</summary>
internal sealed class FamilyMatcher
{
    private const string Resource = "Bootrix.Core.Images.Families.family-rules.json";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly List<CompiledRule> _rules;

    public FamilyMatcher(FamilyRuleSet rules)
    {
        _rules = [.. rules.Rules.Select((rule, order) => new CompiledRule(rule, order)).OrderByDescending(r => r.Rule.Priority).ThenBy(r => r.Order)];
    }

    public static FamilyMatcher Default { get; } = LoadEmbedded();

    public static FamilyMatcher Load(Stream json) =>
        new(JsonSerializer.Deserialize<FamilyRuleSet>(json, CoreJson.Options) ?? new FamilyRuleSet());

    private static FamilyMatcher LoadEmbedded()
    {
        using var stream = typeof(FamilyMatcher).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The embedded family rules are missing.");
        return Load(stream);
    }

    public FamilyMatch? Match(FamilyContext context)
    {
        foreach (var compiled in _rules)
        {
            if (Matches(compiled, context))
            {
                return new FamilyMatch(compiled.Rule.Family, compiled.Rule.Kind);
            }
        }

        return null;
    }

    private static bool Matches(CompiledRule compiled, FamilyContext context)
    {
        var rule = compiled.Rule;

        return ContainerMatches(rule, context)
            && (compiled.Label is null || (context.Label is not null && compiled.Label.IsMatch(context.Label)))
            && (compiled.FileName is null || (context.FileName is not null && compiled.FileName.IsMatch(Path.GetFileName(context.FileName))))
            && (rule.ImageSizes.Length == 0 || rule.ImageSizes.Contains(context.ImageBytes))
            && rule.All.All(glob => context.Trees.Any(tree => tree.Matches(glob)))
            && (rule.Any.Length == 0 || rule.Any.Any(glob => context.Trees.Any(tree => tree.Matches(glob))))
            && !rule.None.Any(glob => context.Trees.Any(tree => tree.Matches(glob)))
            && compiled.Text.All(probe => TextMatches(probe, context))
            && LayoutMatches(rule, context.Layout);
    }

    private static bool ContainerMatches(FamilyRule rule, FamilyContext context) => rule.Container switch
    {
        null => true,
        var container when container.Equals("iso", StringComparison.OrdinalIgnoreCase) => context.IsOptical,
        var container when container.Equals("disk", StringComparison.OrdinalIgnoreCase) => !context.IsOptical,
        _ => false,
    };

    private static bool TextMatches(CompiledText probe, FamilyContext context)
    {
        var text = context.ReadText(probe.Path);
        return text is not null && probe.Pattern.IsMatch(text);
    }

    private static bool LayoutMatches(FamilyRule rule, DiskLayout? layout)
    {
        if (rule.PartitionNames.Length == 0 && rule.MbrTypes.Length == 0 && rule.GptTypes.Length == 0)
        {
            return true;
        }

        if (layout is null)
        {
            return false;
        }

        // Partition conditions are alternatives: a rule lists the ways a family shows up in a partition table.
        return layout.GptPartitions.Any(p => rule.PartitionNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase) || rule.GptTypes.Contains(p.TypeId))
            || layout.MbrPartitions.Any(p => rule.MbrTypes.Contains(p.Type));
    }

    private sealed class CompiledRule
    {
        public CompiledRule(FamilyRule rule, int order)
        {
            Rule = rule;
            Order = order;
            Label = Compile(rule.Label, RegexOptions.None);
            FileName = Compile(rule.FileName, RegexOptions.None);
            Text = [.. rule.Text.Select(probe => new CompiledText(probe.Path, Compile(probe.Pattern, RegexOptions.Multiline)!))];
        }

        public FamilyRule Rule { get; }

        public int Order { get; }

        public Regex? Label { get; }

        public Regex? FileName { get; }

        public List<CompiledText> Text { get; }

        private static Regex? Compile(string? pattern, RegexOptions extra) =>
            pattern is null ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | extra, RegexTimeout);
    }

    private sealed record CompiledText(string Path, Regex Pattern);
}
