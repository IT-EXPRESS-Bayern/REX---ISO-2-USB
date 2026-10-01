// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Tests.Workshop;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;

namespace Bootrix.Core.Tests.Presentation;

public class TargetPcViewTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private static readonly Localizer German = new() { Culture = CultureInfo.GetCultureInfo("de-DE") };

    private static TargetPcView ViewOf(string profile, Localizer localizer)
    {
        var info = TargetPcProfiles.All[profile];
        return TargetPcView.From(info, TargetPcAdvisor.Evaluate(info), localizer);
    }

    public static TheoryData<string> ProfileNames => [.. TargetPcProfiles.All.Keys];

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void EveryProfileProducesReadableLinesInBothLanguages(string profile)
    {
        foreach (var localizer in new[] { English, German })
        {
            var view = ViewOf(profile, localizer);

            Assert.NotEmpty(view.Hardware);
            Assert.All(view.Hardware, line => Assert.False(string.IsNullOrWhiteSpace(line.Value)));
            Assert.NotEmpty(view.Requirements);
            Assert.All(view.Requirements, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
            Assert.DoesNotContain("Pc.", view.Verdict, StringComparison.Ordinal);
            Assert.DoesNotContain("Pc.", string.Concat(view.Hardware.Select(l => l.Value)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AModernLaptopIsReportedAsFine()
    {
        var view = ViewOf("ModernLaptop", English);

        Assert.Equal(Windows11Verdict.LikelySupported, view.VerdictKind);
        Assert.Equal("Windows 11 should run", view.Verdict);
        Assert.Contains(view.Hardware, l => l.Label == "Firmware" && l.Value == "UEFI");
    }

    [Fact]
    public void AnOldBiosMachineIsNotOfferedWindows11()
    {
        var view = ViewOf("Core2DuoBios", German);

        Assert.NotEqual(Windows11Verdict.LikelySupported, view.VerdictKind);
        Assert.Contains(view.Hardware, l => l.Label == "Firmware" && l.Value.StartsWith("BIOS", StringComparison.Ordinal));
        Assert.Contains(view.Requirements, r => r.Status == CheckStatus.Fail);
    }

    [Fact]
    public void RequirementStatusIsTranslated()
    {
        var view = ViewOf("ModernLaptop", German);

        Assert.All(view.Requirements.Where(r => r.Status == CheckStatus.Pass), r => Assert.Equal("erfüllt", r.StatusText));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => TargetPcView.From(null!, null!, English));
    }
}
