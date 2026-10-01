// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Images.Families;
using Bootrix.Core.Json;

namespace Bootrix.Core.Tests.Images.Families;

public sealed class FamilyMatcherTests
{
    private static FamilyMatcher Matcher(string rulesJson) => FamilyMatcher.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "version": 1, "rules": [ {{rulesJson}} ] }""")));

    private static ImageFileIndex Tree(params string[] files)
    {
        var index = new ImageFileIndex();
        foreach (var file in files)
        {
            index.AddFile(file, 1);
        }

        return index;
    }

    private static FamilyContext Context(
        string[]? files = null,
        bool optical = true,
        string? label = null,
        string? fileName = null,
        long imageBytes = 0,
        Dictionary<string, string>? texts = null,
        DiskLayout? layout = null,
        ImageFileIndex[]? trees = null) =>
        new()
        {
            IsOptical = optical,
            Label = label,
            FileName = fileName,
            ImageBytes = imageBytes,
            Trees = trees ?? [Tree(files ?? [])],
            ReadText = path => texts?.GetValueOrDefault(path),
            Layout = layout,
        };

    private static string? Family(FamilyMatcher matcher, FamilyContext context) => matcher.Match(context)?.Family;

    [Fact]
    public void Match_HigherPriorityWinsRegardlessOfOrder()
    {
        var matcher = Matcher("""
            { "family": "low", "priority": 10, "all": ["a"] },
            { "family": "high", "priority": 90, "all": ["a"] }
            """);

        Assert.Equal("high", Family(matcher, Context(["a"])));
    }

    [Fact]
    public void Match_EqualPriorityKeepsTheOrderOfTheFile()
    {
        var matcher = Matcher("""
            { "family": "first", "priority": 50, "all": ["a"] },
            { "family": "second", "priority": 50, "all": ["a"] }
            """);

        Assert.Equal("first", Family(matcher, Context(["a"])));
    }

    [Fact]
    public void Match_NothingApplies_ReturnsNull()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "all": ["missing"] }""");

        Assert.Null(matcher.Match(Context(["other"])));
    }

    [Fact]
    public void Match_ReturnsTheKindOfTheRule()
    {
        var matcher = Matcher("""{ "family": "w", "kind": "WindowsPe", "priority": 1, "all": ["a"] }""");

        Assert.Equal(new FamilyMatch("w", "WindowsPe"), matcher.Match(Context(["a"])));
    }

    [Fact]
    public void Match_KindDefaultsToLinux()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "all": ["a"] }""");

        Assert.Equal("Linux", matcher.Match(Context(["a"]))!.Kind);
    }

    [Fact]
    public void All_RequiresEveryPattern()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "all": ["a", "b/*.cfg"] }""");

        Assert.Equal("x", Family(matcher, Context(["a", "b/c.cfg"])));
        Assert.Null(matcher.Match(Context(["a", "b/c.txt"])));
    }

    [Fact]
    public void Any_RequiresAtLeastOnePattern()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "any": ["a", "b"] }""");

        Assert.Equal("x", Family(matcher, Context(["b"])));
        Assert.Null(matcher.Match(Context(["c"])));
    }

    [Fact]
    public void None_ExcludesTheRuleWhenAnyPatternMatches()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "all": ["a"], "none": ["b", "c/**"] }""");

        Assert.Equal("x", Family(matcher, Context(["a"])));
        Assert.Null(matcher.Match(Context(["a", "b"])));
        Assert.Null(matcher.Match(Context(["a", "c/d/e"])));
    }

    [Fact]
    public void Patterns_MaySpanSeveralTrees()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "all": ["a", "b"] }""");

        Assert.Equal("x", Family(matcher, Context(trees: [Tree("a"), Tree("b")])));
    }

    [Fact]
    public void Label_IsAnUnanchoredCaseInsensitiveRegex()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "label": "^ubuntu \\d" }""");

        Assert.Equal("x", Family(matcher, Context(label: "UBUNTU 24.04")));
        Assert.Null(matcher.Match(Context(label: "Kubuntu 24.04")));
        Assert.Null(matcher.Match(Context(label: null)));
    }

    [Fact]
    public void FileName_IsMatchedAgainstTheNameWithoutDirectory()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "fileName": "^tails-.*\\.img$" }""");

        Assert.Equal("x", Family(matcher, Context(fileName: "/downloads/Tails-amd64-6.0.img")));
        Assert.Null(matcher.Match(Context(fileName: "/tails-dir/other.img")));
        Assert.Null(matcher.Match(Context(fileName: null)));
    }

    [Fact]
    public void Text_NeedsTheFileAndAMatchingLine()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "text": [{ "path": ".disk/info", "pattern": "^Debian" }] }""");

        Assert.Equal("x", Family(matcher, Context(texts: new() { [".disk/info"] = "something\nDebian GNU/Linux 12" })));
        Assert.Null(matcher.Match(Context(texts: new() { [".disk/info"] = "Not Debian" })));
        Assert.Null(matcher.Match(Context()));
    }

    [Fact]
    public void Text_ProbesMustAllMatch()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "text": [{ "path": "a", "pattern": "one" }, { "path": "b", "pattern": "two" }] }""");

        Assert.Equal("x", Family(matcher, Context(texts: new() { ["a"] = "one", ["b"] = "two" })));
        Assert.Null(matcher.Match(Context(texts: new() { ["a"] = "one", ["b"] = "three" })));
    }

    [Fact]
    public void Container_SeparatesOpticalFromDiskImages()
    {
        var matcher = Matcher("""
            { "family": "iso-only", "priority": 2, "container": "iso", "all": ["a"] },
            { "family": "disk-only", "priority": 1, "container": "disk", "all": ["a"] }
            """);

        Assert.Equal("iso-only", Family(matcher, Context(["a"], optical: true)));
        Assert.Equal("disk-only", Family(matcher, Context(["a"], optical: false)));
    }

    [Fact]
    public void Container_UnknownValue_NeverMatches()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "container": "tape" }""");

        Assert.Null(matcher.Match(Context()));
    }

    [Fact]
    public void ImageSizes_MustEqualOneOfTheListedSizes()
    {
        var matcher = Matcher("""{ "family": "floppy", "priority": 1, "imageSizes": [1474560, 737280] }""");

        Assert.Equal("floppy", Family(matcher, Context(imageBytes: 1_474_560)));
        Assert.Null(matcher.Match(Context(imageBytes: 1_474_561)));
    }

    private static DiskLayout Layout(
        IEnumerable<MbrPartition>? mbr = null,
        IEnumerable<GptPartition>? gpt = null) =>
        new() { MbrPartitions = [.. mbr ?? []], GptPartitions = [.. gpt ?? []] };

    private static GptPartition GptEntry(string name, Guid? type = null) => new(type ?? Guid.NewGuid(), Guid.NewGuid(), 2048, 4095, name);

    [Fact]
    public void PartitionNames_MatchByNameIgnoringCase()
    {
        var matcher = Matcher("""{ "family": "tails", "priority": 1, "container": "disk", "partitionNames": ["Tails"] }""");

        Assert.Equal("tails", Family(matcher, Context(optical: false, layout: Layout(gpt: [GptEntry("TAILS")]))));
        Assert.Null(matcher.Match(Context(optical: false, layout: Layout(gpt: [GptEntry("Data")]))));
        Assert.Null(matcher.Match(Context(optical: false)));
    }

    [Fact]
    public void GptAndMbrTypes_AreAlternatives()
    {
        var freebsd = Guid.Parse("516e7cb6-6ecf-11d6-8ff8-00022d09712b");
        var matcher = Matcher("""{ "family": "bsd", "priority": 1, "gptTypes": ["516e7cb6-6ecf-11d6-8ff8-00022d09712b"], "mbrTypes": [165] }""");

        Assert.Equal("bsd", Family(matcher, Context(optical: false, layout: Layout(gpt: [GptEntry("x", freebsd)]))));
        Assert.Equal("bsd", Family(matcher, Context(optical: false, layout: Layout(mbr: [new MbrPartition(0, 165, false, 2048, 100)]))));
        Assert.Null(matcher.Match(Context(optical: false, layout: Layout(mbr: [new MbrPartition(0, 131, false, 2048, 100)]))));
    }

    [Fact]
    public void FileConditionsAndPartitionConditions_MustHoldTogether()
    {
        var matcher = Matcher("""{ "family": "x", "priority": 1, "all": ["a"], "partitionNames": ["P"] }""");

        Assert.Null(matcher.Match(Context(["a"], optical: false)));
        Assert.Null(matcher.Match(Context(["b"], optical: false, layout: Layout(gpt: [GptEntry("P")]))));
        Assert.Equal("x", Family(matcher, Context(["a"], optical: false, layout: Layout(gpt: [GptEntry("P")]))));
    }

    private static FamilyRuleSet EmbeddedRules()
    {
        using var stream = typeof(FamilyMatcher).Assembly.GetManifestResourceStream("Bootrix.Core.Images.Families.family-rules.json")!;
        return JsonSerializer.Deserialize<FamilyRuleSet>(stream, CoreJson.Options)!;
    }

    [Fact]
    public void EmbeddedRules_AreWellFormed()
    {
        var rules = EmbeddedRules().Rules;

        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Family));
            Assert.True(rule.Priority > 0, rule.Family);
            Assert.True(rule.Kind == "Linux" || Enum.TryParse<ImageKind>(rule.Kind, out _), $"{rule.Family}: kind {rule.Kind}");
            Assert.True(rule.Container is null or "iso" or "disk", $"{rule.Family}: container {rule.Container}");

            foreach (var pattern in new[] { rule.Label, rule.FileName }.Concat(rule.Text.Select(t => t.Pattern)).OfType<string>())
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            foreach (var glob in rule.All.Concat(rule.Any).Concat(rule.None))
            {
                Assert.NotNull(PathGlob.Get(glob));
            }

            Assert.All(rule.Text, t => Assert.False(string.IsNullOrEmpty(t.Path)));

            // A rule without any condition would claim every image.
            Assert.True(
                rule.All.Length + rule.Any.Length + rule.Text.Length + rule.PartitionNames.Length + rule.MbrTypes.Length + rule.GptTypes.Length + rule.ImageSizes.Length > 0
                || rule.Label is not null || rule.FileName is not null,
                $"{rule.Family} has no condition");
        }
    }

    [Fact]
    public void DefaultMatcher_LoadsTheEmbeddedRules() => Assert.NotNull(FamilyMatcher.Default);

    [Fact]
    public void EmbeddedRules_ClassifyTheWindowsMarkers()
    {
        var matcher = FamilyMatcher.Default;

        Assert.Equal("windows", Family(matcher, Context(["sources/install.wim", "bootmgr"])));
        Assert.Equal("winpe", Family(matcher, Context(["sources/boot.wim", "bootmgr", "boot/bcd"])));
        Assert.Equal("windows-nt5", Family(matcher, Context(["$WIN_NT$.~BT/setupldr.bin"])));
        Assert.Equal("windows", Family(matcher, Context(["sources/setup.exe", "bootmgr"])));
        Assert.Equal("winpe", Family(matcher, Context(["sources/setup.exe", "sources/boot.wim", "bootmgr"])));
    }
}
