// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Localization;
using Bootrix.Core.Engine;
using Bootrix.Core.Presentation;
using Bootrix.Core.Tiny;

namespace Bootrix.Core.Tests.Presentation;

public class TinyViewTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private static readonly Localizer German = new() { Culture = CultureInfo.GetCultureInfo("de-DE") };

    public static TheoryData<string> ProfileIds => [.. TinyProfiles.BuiltInIds];

    [Theory]
    [MemberData(nameof(ProfileIds))]
    public void EveryProfileAndEveryGroupHasATextInBothLanguages(string id)
    {
        foreach (var localizer in new[] { English, German })
        {
            Assert.True(localizer.Has("Tiny.Profile." + id), id);
            Assert.True(localizer.Has("Tiny.Profile." + id + ".Desc"), id);
            foreach (var group in TinyProfiles.Load(id).Groups.Where(g => g.Id != TinyView.HardwareBypassGroup))
            {
                Assert.True(localizer.Has("Tiny.Group." + group.Id), group.Id);
                Assert.True(localizer.Has("Tiny.Group." + group.Id + ".Hint"), group.Id);
            }
        }
    }

    [Fact]
    public void TheHardwareCheckIsOneSwitchNotAGroup()
    {
        var groups = TinyView.Groups(TinyProfiles.Load("tiny11"), English);

        Assert.DoesNotContain(groups, g => g.Id == TinyView.HardwareBypassGroup);
        Assert.Contains(groups, g => g.Id == "edge" && g.DefaultOn);
        Assert.Contains(groups, g => g.Id == "tools" && !g.DefaultOn);
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Title)));
    }

    [Fact]
    public void EditionsAreShownWithArchitectureAndSize()
    {
        var edition = new WimEdition { Index = 6, Name = "Windows 11 Pro", Arch = WindowsArch.X64, TotalBytes = 20L << 30 };

        Assert.Equal("Windows 11 Pro (X64, 20 GB)", TinyView.EditionLabel(edition, English));
        Assert.Equal("6", TinyView.EditionLabel(new WimEdition { Index = 6 }, English));
    }

    private static TinySelection Selection(string profile = "tiny11") => new() { SourcePath = @"C:\in\win.iso", OutputPath = @"C:\out\tiny.iso", ProfileId = profile };

    [Fact]
    public void DefaultsRequestNothingSpecial()
    {
        var request = TinyRequestBuilder.Create(Selection());

        Assert.Empty(request.KeepGroups);
        Assert.Empty(request.IncludeGroups);
        Assert.True(request.BypassHardwareChecks);
        Assert.Null(request.Edition);
        Assert.Null(request.Unattend);
        Assert.Equal("TINY", request.VolumeLabel);
    }

    [Fact]
    public void SwitchedOffGroupsAreKeptAndSwitchedOnOptionalGroupsAreIncluded()
    {
        var request = TinyRequestBuilder.Create(Selection() with
        {
            Groups = new Dictionary<string, bool> { ["edge"] = false, ["tools"] = true, ["onedrive"] = true },
        });

        Assert.Equal(["edge"], request.KeepGroups);
        Assert.Equal(["tools"], request.IncludeGroups);
    }

    [Fact]
    public void WithoutTheBypassTheHardwareGroupIsKeptToo()
    {
        var request = TinyRequestBuilder.Create(Selection() with { SkipHardwareChecks = false });

        Assert.False(request.BypassHardwareChecks);
        Assert.Contains(TinyView.HardwareBypassGroup, request.KeepGroups);
    }

    [Fact]
    public void TheRequestIsAcceptedByTheProfileRules()
    {
        foreach (var id in TinyProfiles.BuiltInIds)
        {
            var profile = TinyProfiles.Load(id);
            var request = TinyRequestBuilder.Create(Selection(id) with { Groups = profile.Groups.ToDictionary(g => g.Id, g => !g.Default) });

            // Unknown group names would be an error here.
            Assert.NotNull(TinyProfiles.DisabledGroups(profile, request.KeepGroups, request.IncludeGroups));
        }
    }

    [Fact]
    public void EditionAccountLabelAndCompressionAreCarriedOver()
    {
        var request = TinyRequestBuilder.Create(Selection() with
        {
            EditionIndex = 6,
            VolumeLabel = "  WERKSTATT ",
            ForFat32 = true,
            LocalAccountName = " Anna ",
            AcknowledgeNoServicing = true,
        });

        Assert.Equal("6", request.Edition);
        Assert.Equal("WERKSTATT", request.VolumeLabel);
        Assert.Equal(InstallImageCompression.Maximum, request.Compression);
        Assert.Equal("Anna", request.Unattend!.Windows.LocalAccountName);
        Assert.True(request.AcknowledgeNoServicing);
    }

    [Fact]
    public void BuiltRequestsPassTheBrokerValidation()
    {
        var request = TinyRequestBuilder.Create(Selection() with { EditionIndex = 3, LocalAccountName = "Anna" });

        Assert.Empty(EngineRequestValidator.Validate(request));
    }
}
