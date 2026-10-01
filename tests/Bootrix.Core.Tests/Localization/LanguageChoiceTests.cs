// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;

namespace Bootrix.Core.Tests.Localization;

public class LanguageChoiceTests
{
    [Theory]
    [InlineData("", "de-AT", "de-AT")]
    [InlineData("", "fr-FR", "en-US")]
    [InlineData("", "en-GB", "en-GB")]
    [InlineData(null, "de", "de-DE")]
    [InlineData("en-US", "de-DE", "en-US")]
    [InlineData("de-DE", "en-US", "de-DE")]
    [InlineData("fr-FR", "de-DE", "en-US")]
    [InlineData("not a culture!", "de-DE", "de-DE")]
    public void Resolve_FollowsTheSettingThenTheSystemAndFallsBackToEnglish(string? setting, string system, string expected)
    {
        var culture = LanguageChoice.Resolve(setting, CultureInfo.GetCultureInfo(system));

        Assert.Equal(expected, culture.Name);
    }
}
