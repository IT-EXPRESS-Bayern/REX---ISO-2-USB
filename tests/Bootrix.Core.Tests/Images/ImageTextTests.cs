// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Localization;

namespace Bootrix.Core.Tests.Images;

public partial class ImageTextTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly string[] PolicyLists = ["reasons", "warnings"];

    private static List<string> KeysOf(Type type) =>
        [.. type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];

    private static List<string> AllKeys() => [.. KeysOf(typeof(ImageWarningKeys)), .. KeysOf(typeof(ImagePolicyKeys))];

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    private static string Text(string key, CultureInfo culture) => new Localizer { Culture = culture }.Get(key);

    [Fact]
    public void EveryKey_HasGermanAndEnglishText()
    {
        var keys = AllKeys();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(new Localizer { Culture = German }.Has(key), $"{key} missing in German");
            Assert.True(new Localizer { Culture = English }.Has(key), $"{key} missing in English");
            Assert.NotEqual(Text(key, German), Text(key, English));
        }
    }

    [Fact]
    public void BothLanguages_UseTheSamePlaceholders()
    {
        foreach (var key in AllKeys())
        {
            var german = Placeholder().Matches(Text(key, German)).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
            var english = Placeholder().Matches(Text(key, English)).Select(m => m.Groups[1].Value).Distinct().Order().ToList();

            Assert.Equal(german, english);
        }
    }

    [Fact]
    public void ThereAreNoOrphanedTexts()
    {
        var manager = new ResourceManager("Bootrix.Core.Resources.Images", typeof(Localizer).Assembly);
        var set = manager.GetResourceSet(German, createIfNotExists: true, tryParents: true)!;
        var defined = set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(AllKeys().Order(StringComparer.Ordinal), defined.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KeysNamedInThePolicyData_HaveTexts()
    {
        using var stream = typeof(ImagePolicy).Assembly.GetManifestResourceStream("Bootrix.Core.Images.Policy.image-policy.json")!;
        using var document = JsonDocument.Parse(stream);

        var referenced = document.RootElement.GetProperty("families").EnumerateObject()
            .SelectMany(family => PolicyLists
                .Where(name => family.Value.TryGetProperty(name, out _))
                .SelectMany(name => family.Value.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)))
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, key => Assert.Contains(key, AllKeys()));
    }

    [Fact]
    public void Format_UsesTheArgumentsOfTheFinding()
    {
        var warning = new ImageWarning(ImageWarningKeys.IsoTruncated, WarningSeverity.Error, 4_700_000_000L, 1_000L);

        var german = warning.Format(new Localizer { Culture = German });
        var english = warning.Format(new Localizer { Culture = English });

        Assert.Contains("4.700.000.000", german, StringComparison.Ordinal);
        Assert.Contains("4,700,000,000", english, StringComparison.Ordinal);
        Assert.Contains("1,000", english, StringComparison.Ordinal);
    }
}
