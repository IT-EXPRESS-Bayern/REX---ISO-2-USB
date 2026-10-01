// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Localization;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

public class LinuxNoticeKeysTests
{
    private static readonly string[] Keys =
    [
        .. typeof(LinuxNoticeKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!),
    ];

    private static Localizer In(string culture) => new() { Culture = CultureInfo.GetCultureInfo(culture) };

    [Fact]
    public void EveryNotice_HasAGermanAndAnEnglishText()
    {
        Assert.NotEmpty(Keys);
        foreach (var key in Keys)
        {
            Assert.True(In("de").Has(key), "de " + key);
            Assert.NotEqual(key, In("de").Get(key));
            Assert.NotEqual(key, In("en").Get(key));
        }
    }

    [Fact]
    public void BothLanguages_UseTheSamePlaceholders()
    {
        foreach (var key in Keys)
        {
            Assert.Equal(Placeholders(In("de").Get(key)), Placeholders(In("en").Get(key)));
        }
    }

    [Fact]
    public void Placeholders_FitTheArgumentsTheBuilderPasses()
    {
        // Spot checks of the keys that carry values; a text with {2} where only two values are given would print a literal.
        Assert.Equal("{0},{1},{2}", Placeholders(In("en").Get(LinuxNoticeKeys.LabelRewritten)));
        Assert.Equal("{0},{1}", Placeholders(In("en").Get(LinuxNoticeKeys.FileSkipped)));
        Assert.Equal("{0},{1}", Placeholders(In("en").Get(LinuxNoticeKeys.PersistenceParameterAdded)));
        Assert.Equal("{0},{1}", Placeholders(In("en").Get(LinuxNoticeKeys.SyslinuxInstalled)));
        Assert.Equal("{0},{1}", Placeholders(In("en").Get(LinuxNoticeKeys.SyslinuxVersionDiffers)));
    }

    private static string Placeholders(string text) =>
        string.Join(',', Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));
}
