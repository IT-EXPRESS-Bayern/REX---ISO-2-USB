// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Bootrix.Core.Localization;
using Bootrix.Core.Workshop.Advice;

namespace Bootrix.Core.Tests.Workshop;

public class AdvisorKeysTests
{
    private static IEnumerable<string> AllKeys() =>
        typeof(AdvisorKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static Localizer For(string culture) => new() { Culture = CultureInfo.GetCultureInfo(culture) };

    [Fact]
    public void EveryKeyHasAGermanAndAnEnglishText()
    {
        foreach (var key in AllKeys())
        {
            foreach (var culture in new[] { "de", "en" })
            {
                Assert.True(For(culture).Has(key), $"{key} missing for {culture}");
            }
        }
    }

    [Fact]
    public void GermanAndEnglishTextsDiffer()
    {
        // Catches an English text pasted into the German file or the other way round.
        foreach (var key in AllKeys())
        {
            Assert.NotEqual(For("de").Get(key), For("en").Get(key));
        }
    }

    [Fact]
    public void KeysAreUnique()
    {
        var keys = AllKeys().ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void EveryWorkshopTextInTheResourcesHasAKey()
    {
        var manager = new ResourceManager("Bootrix.Core.Resources.Strings", typeof(Localizer).Assembly);
        var known = AllKeys().ToHashSet();

        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en") })
        {
            var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
            var orphans = set.Cast<DictionaryEntry>()
                .Select(e => (string)e.Key)
                .Where(k => k.StartsWith("Workshop.", StringComparison.Ordinal) && !known.Contains(k))
                .ToList();

            Assert.Empty(orphans);
        }
    }

    [Fact]
    public void PlaceholdersMatchBetweenTheLanguages()
    {
        foreach (var key in AllKeys())
        {
            Assert.Equal(Placeholders(For("de").Get(key)), Placeholders(For("en").Get(key)));
        }
    }

    [Theory]
    [InlineData("de", "Der Prozessor „Intel Core 2“ gehört nach seinem Namen vermutlich nicht")]
    [InlineData("en", "Judging by its name, the processor \"Intel Core 2\" is probably not")]
    public void ArgumentsAreSubstitutedInTheCultureOfTheLocalizer(string culture, string expected)
    {
        var text = new AdvisorMessage(AdvisorKeys.CpuModelFail, AdvisorSeverity.Warning, "Intel Core 2").Describe(For(culture));

        Assert.StartsWith(expected, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de", "3,9 GB")]
    [InlineData("en", "3.9 GB")]
    public void NumbersFollowTheLanguageOfTheText(string culture, string expected)
    {
        var text = new AdvisorMessage(AdvisorKeys.RamBorderline, AdvisorSeverity.Info, 3.9).Describe(For(culture));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void DatesFollowTheLanguageOfTheText()
    {
        var message = new AdvisorMessage(AdvisorKeys.LifecycleRunningVersionEnding, AdvisorSeverity.Info, "Windows 11 24H2", new DateOnly(2026, 10, 13));

        Assert.Contains("13.10.2026", message.Describe(For("de")), StringComparison.Ordinal);
        Assert.Contains("10/13/2026", message.Describe(For("en")), StringComparison.Ordinal);
    }

    private static string Placeholders(string text) =>
        string.Join(',', System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));
}
