// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Tests.Engine;

public class TinyBuildRequestValidatorTests
{
    private static TinyBuildJobRequest Valid() => new()
    {
        IsoPath = @"C:\Users\Anna\Downloads\Win11_24H2_German_x64.iso",
        OutputIsoPath = @"C:\Users\Anna\Desktop\tiny11.iso",
        ProfileId = "tiny11",
        Edition = "Pro",
    };

    [Fact]
    public void WellFormedRequest_HasNoProblems()
    {
        Assert.Empty(EngineRequestValidator.Validate(Valid()));
        Assert.Empty(EngineRequestValidator.Validate(Valid() with { KeepGroups = ["edge", "onedrive"], IncludeGroups = ["tools"] }));
    }

    [Theory]
    [InlineData(@"relative.iso")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\images\source.img")]
    [InlineData(@"C:\images\source.iso:stream")]
    public void SourceMustBeAnAbsoluteIsoFile(string path)
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { IsoPath = path }));
    }

    [Theory]
    [InlineData(@"relative.iso")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\.\pipe\x.iso")]
    [InlineData(@"C:\out\..\Windows\x.iso")]
    public void OutputMustBeAnAbsoluteIsoFile(string path)
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { OutputIsoPath = path }));
    }

    [Fact]
    public void OutputMayNotBeTheSource()
    {
        var request = Valid() with { OutputIsoPath = @"c:\USERS\anna\downloads\win11_24h2_german_x64.ISO" };

        Assert.Contains("output image would overwrite the source image", EngineRequestValidator.Validate(request));
    }

    [Fact]
    public void TheWorkFolderIsTheEnginesChoice()
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { WorkDirectory = @"C:\Windows\System32" }));
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { KeepWorkDirectory = true }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../tiny11")]
    [InlineData("tiny 11")]
    [InlineData("tiny11\"")]
    public void ProfileIdsAreSimpleNames(string id)
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { ProfileId = id }));
    }

    [Fact]
    public void OptionGroupsAreSimpleNamesAndFew()
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { KeepGroups = ["edge;calc"] }));
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { IncludeGroups = [.. Enumerable.Range(0, 33).Select(i => "g" + i)] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890123")]
    [InlineData("TI\nNY")]
    public void VolumeLabelHasToFit(string label)
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { VolumeLabel = label }));
    }

    [Fact]
    public void EditionIsShortAndPlain()
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { Edition = new string('x', 129) }));
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { Edition = "Pro\r\nHome" }));
    }

    [Fact]
    public void AnswerFileValuesAreChecked()
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { Unattend = new UnattendOptions { LocalAccountPassword = new string('p', 128) } }));
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { Unattend = new UnattendOptions { ComputerName = "ABCDEFGHIJKLMNOP" } }));
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { Unattend = new UnattendOptions { FirstLogonCommands = ["echo a\r\necho b"] } }));
        Assert.NotEmpty(EngineRequestValidator.Validate(Valid() with { Unattend = new UnattendOptions { SpecializeCommands = [.. Enumerable.Repeat("echo", 65)] } }));
        Assert.Empty(EngineRequestValidator.Validate(Valid() with { Unattend = new UnattendOptions { ComputerName = "WERKSTATT-01", FirstLogonCommands = ["winget upgrade --all"] } }));
    }

    [Fact]
    public void TheRequestSurvivesTheWire()
    {
        var json = System.Text.Json.JsonSerializer.Serialize<EngineJobRequest>(Valid(), Bootrix.Core.Ipc.IpcJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<EngineJobRequest>(json, Bootrix.Core.Ipc.IpcJson.Options);

        var request = Assert.IsType<TinyBuildJobRequest>(back);
        Assert.Equal(Valid().IsoPath, request.IsoPath);
        Assert.Equal(Valid().OutputIsoPath, request.OutputIsoPath);
        Assert.Equal("Pro", request.Edition);
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize<EngineJobRequest>(request, Bootrix.Core.Ipc.IpcJson.Options));
        Assert.Contains("\"tiny-build\"", json, StringComparison.Ordinal);
    }
}
