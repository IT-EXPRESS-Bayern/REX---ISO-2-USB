// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public partial class PlanLocalizationTests
{
    private static Localizer For(string culture) => new() { Culture = CultureInfo.GetCultureInfo(culture) };

    public static TheoryData<string> WarningCodes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var code in PlanWarningCodeList.All.Order(StringComparer.Ordinal))
            {
                data.Add(code);
            }

            return data;
        }
    }

    public static TheoryData<ErrorCode> PlannerErrors => new()
    {
        ErrorCode.SectorSizeUnsupported,
        ErrorCode.FileSystemUnsupported,
        ErrorCode.FileTooLargeForFileSystem,
        ErrorCode.FileSystemTooSmall,
        ErrorCode.WriteModeUnsupported,
        ErrorCode.PersistenceTooSmall,
        ErrorCode.DeviceTooSmall,
    };

    [Theory]
    [MemberData(nameof(WarningCodes))]
    public void EveryWarning_HasGermanAndEnglishTextWithTheSamePlaceholders(string code)
    {
        var german = For("de").Get(code);
        var english = For("en").Get(code);

        Assert.NotEqual(code, german);
        Assert.NotEqual(code, english);
        Assert.NotEqual(german, english);
        Assert.Equal(Placeholders(german), Placeholders(english));
    }

    [Theory]
    [MemberData(nameof(WarningCodes))]
    public void EveryWarning_FormatsWithItsArguments(string code)
    {
        var count = Placeholders(For("en").Get(code)).Count;
        var args = Enumerable.Range(0, count).Select(i => (object?)$"<{i}>").ToArray();
        var warning = new PlanWarning(code, args);

        foreach (var culture in new[] { "de", "en" })
        {
            var text = warning.Format(For(culture));

            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            for (var i = 0; i < count; i++)
            {
                Assert.Contains($"<{i}>", text, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PlannerErrors))]
    public void PlannerErrors_HaveMatchingPlaceholdersInBothLanguages(ErrorCode code)
    {
        foreach (var part in new[] { "Cause", "Action" })
        {
            var german = For("de").Get($"Error.{code}.{part}");
            var english = For("en").Get($"Error.{code}.{part}");

            Assert.Equal(Placeholders(german), Placeholders(english));
        }
    }

    [Fact]
    public void RaisedWarnings_AreFormattedFromTheirOwnArguments()
    {
        var plan = LayoutPlanner.Plan(WindowsIso(), new TargetOptions(), Stick(3 * Tib));

        var capped = plan.Warnings.Single(w => w.Code == PlanWarningCodes.CapacityNotUsed);

        Assert.Equal(["2 TiB", "1 TiB"], capped.Args);
        Assert.Equal("Das Dateisystem ist auf 2 TiB begrenzt; 1 TiB des Datenträgers bleiben ungenutzt.", capped.Format(For("de")));
        Assert.Equal("The file system is limited to 2 TiB; 1 TiB of the device stay unused.", capped.Format(For("en")));
    }

    [Fact]
    public void DeviceTooSmall_DescribesRequiredAndAvailableSizeInBothLanguages()
    {
        var ex = Assert.Throws<BootrixException>(() => LayoutPlanner.Plan(WindowsIso(), new TargetOptions(), Stick(2 * Gib)));

        var german = ErrorCatalog.Describe(ex, For("de"));
        var english = ErrorCatalog.Describe(ex, For("en"));

        Assert.Contains("2 GiB", german.Cause, StringComparison.Ordinal);
        Assert.Contains("2 GiB", english.Cause, StringComparison.Ordinal);
        Assert.Contains("zu klein", german.Cause, StringComparison.Ordinal);
        Assert.Contains("too small", english.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTooLarge_NamesTheFileSystemAndTheSize()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            LayoutPlanner.Plan(LinuxIso(big: true), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Stick(32 * Gib)));

        var text = ErrorCatalog.Describe(ex, For("en")).Cause;

        Assert.Equal("The image contains a file of 5 GiB that does not fit Fat32 (limit 4 GiB).", text);
    }

    [Fact]
    public void SizeText_UsesBinaryUnitsWithInvariantNumbers()
    {
        var plan = LayoutPlanner.Plan(Data(), new TargetOptions { FileSystem = FileSystemKind.Fat16 }, Stick(8 * Gib));

        var warning = plan.Warnings.Single(w => w.Code == PlanWarningCodes.CapacityNotUsed);

        Assert.Equal("3.91 GiB", warning.Args[0]);
        Assert.Equal("4.09 GiB", warning.Args[1]);
    }

    private static List<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
