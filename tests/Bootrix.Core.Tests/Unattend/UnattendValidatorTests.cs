// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Tests.Unattend;

public class UnattendValidatorTests
{
    [Theory]
    [InlineData("Kunde")]
    [InlineData("Max Mustermann")]
    [InlineData("Müller-Lüdenscheidt")]
    [InlineData("a")]
    [InlineData("12345678901234567890")]
    public void AcceptsGoodAccountNames(string name)
    {
        Assert.Empty(UnattendValidator.ValidateAccountName(name));
    }

    [Theory]
    [InlineData("", "Validation.Account.Empty")]
    [InlineData("   ", "Validation.Account.Empty")]
    [InlineData("123456789012345678901", "Validation.Account.TooLong")]
    [InlineData("a/b", "Validation.Account.InvalidChars")]
    [InlineData("a@b", "Validation.Account.InvalidChars")]
    [InlineData("tab\there", "Validation.Account.InvalidChars")]
    [InlineData("...", "Validation.Account.OnlyDotsOrSpaces")]
    [InlineData("Administrator", "Validation.Account.Reserved")]
    [InlineData("GUEST", "Validation.Account.Reserved")]
    [InlineData("defaultuser0", "Validation.Account.Reserved")]
    public void RejectsBadAccountNames(string name, string expectedKey)
    {
        Assert.Contains(UnattendValidator.ValidateAccountName(name), i => i.Key == expectedKey);
    }

    [Fact]
    public void AccountNameMustDifferFromComputerName()
    {
        Assert.Contains(UnattendValidator.ValidateAccountName("pc-01", "PC-01"), i => i.Key == "Validation.Account.SameAsComputer");
    }

    [Theory]
    [InlineData("PC-01")]
    [InlineData("A")]
    [InlineData("ABCDEFGHIJKLMNO")]
    [InlineData("kunde-12ab")]
    public void AcceptsGoodComputerNames(string name)
    {
        Assert.Empty(UnattendValidator.ValidateComputerName(name));
    }

    [Theory]
    [InlineData(null, "Validation.Computer.Empty")]
    [InlineData("", "Validation.Computer.Empty")]
    [InlineData("ABCDEFGHIJKLMNOP", "Validation.Computer.TooLong")]
    [InlineData("PC_01", "Validation.Computer.InvalidChars")]
    [InlineData("PC 01", "Validation.Computer.InvalidChars")]
    [InlineData("12345", "Validation.Computer.OnlyDigits")]
    [InlineData("-PC", "Validation.Computer.HyphenEdge")]
    [InlineData("PC-", "Validation.Computer.HyphenEdge")]
    public void RejectsBadComputerNames(string? name, string expectedKey)
    {
        Assert.Contains(UnattendValidator.ValidateComputerName(name), i => i.Key == expectedKey);
    }

    [Fact]
    public void IssuesAreLocalizedInBothLanguages()
    {
        var issue = UnattendValidator.ValidateAccountName("Administrator")[0];

        var german = issue.Describe(new Localizer { Culture = CultureInfo.GetCultureInfo("de") });
        var english = issue.Describe(new Localizer { Culture = CultureInfo.GetCultureInfo("en") });

        Assert.Contains("reserviert", german, StringComparison.Ordinal);
        Assert.Contains("reserved", english, StringComparison.Ordinal);
        Assert.Contains("Administrator", english, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryValidationKeyExistsInBothLanguages()
    {
        var keys = new[]
        {
            "Validation.Account.Empty", "Validation.Account.TooLong", "Validation.Account.InvalidChars",
            "Validation.Account.OnlyDotsOrSpaces", "Validation.Account.Reserved", "Validation.Account.SameAsComputer",
            "Validation.Computer.Empty", "Validation.Computer.TooLong", "Validation.Computer.InvalidChars",
            "Validation.Computer.OnlyDigits", "Validation.Computer.HyphenEdge", "Validation.Password.PlainTextInFile",
        };

        foreach (var culture in new[] { "de", "en" })
        {
            var localizer = new Localizer { Culture = CultureInfo.GetCultureInfo(culture) };
            Assert.All(keys, key => Assert.True(localizer.Has(key), $"{key} missing for {culture}"));
        }
    }
}
