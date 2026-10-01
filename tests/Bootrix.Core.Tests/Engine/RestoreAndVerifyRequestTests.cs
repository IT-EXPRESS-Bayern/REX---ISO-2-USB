// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Engine;
using Bootrix.Core.Json;
using Bootrix.Core.Model;
using Bootrix.Core.Writing.Raw;
using Bootrix.Core.Writing.Verify;

namespace Bootrix.Core.Tests.Engine;

public class RestoreAndVerifyRequestTests
{
    private static EngineTarget Target() => new(TestPaths.Disk3, TestPaths.IdentityOf(TestPaths.Disk3));

    private static T RoundTrip<T>(T value) where T : EngineJobRequest
    {
        var json = JsonSerializer.Serialize<EngineJobRequest>(value, CoreJson.Options);
        return (T)JsonSerializer.Deserialize<EngineJobRequest>(json, CoreJson.Options)!;
    }

    [Fact]
    public void RestoreRequest_SurvivesTheTripAndNamesItsType()
    {
        var request = new RestoreDriveJobRequest
        {
            Targets = [Target()],
            Scheme = PartitionScheme.Gpt,
            FileSystem = FileSystemKind.ExFat,
            Label = "DATEN",
            ClusterSizeBytes = 32768,
        };

        var json = JsonSerializer.Serialize<EngineJobRequest>(request, CoreJson.Options);
        var copy = RoundTrip(request);

        Assert.Contains("\"restore-drive\"", json, StringComparison.Ordinal);
        Assert.Equal(PartitionScheme.Gpt, copy.Scheme);
        Assert.Equal(FileSystemKind.ExFat, copy.FileSystem);
        Assert.Equal("DATEN", copy.Label);
        Assert.Equal(32768, copy.ClusterSizeBytes);
        Assert.Equal(request.ToOptions(), copy.ToOptions());
    }

    [Fact]
    public void VerifyRequest_SurvivesTheTripAndNamesItsType()
    {
        var request = new VerifyJobRequest
        {
            ImagePath = TestPaths.Image,
            Targets = [Target()],
            Mode = VerifyMode.Files,
            ArchiveEntry = "disk/image.iso",
            BlockMap = BlockMapUse.Off,
        };

        var json = JsonSerializer.Serialize<EngineJobRequest>(request, CoreJson.Options);
        var copy = RoundTrip(request);

        Assert.Contains("\"verify-media\"", json, StringComparison.Ordinal);
        Assert.Equal(VerifyMode.Files, copy.Mode);
        Assert.Equal("disk/image.iso", copy.ArchiveEntry);
        Assert.Equal(BlockMapUse.Off, copy.BlockMap);
    }

    [Fact]
    public void RawAndWriteRequests_CarryTheArchiveEntryAndTheBlockMapChoice()
    {
        var raw = RoundTrip(new RawWriteJobRequest { ImagePath = TestPaths.Image, Targets = [Target()], ArchiveEntry = "a.img", BlockMap = BlockMapUse.FillGaps });
        var write = RoundTrip(new WriteImageJobRequest { ImagePath = TestPaths.Image, Targets = [Target()], ArchiveEntry = "b.img" });

        Assert.Equal("a.img", raw.ArchiveEntry);
        Assert.Equal(BlockMapUse.FillGaps, raw.BlockMap);
        Assert.Equal("b.img", write.ArchiveEntry);
        Assert.Equal(BlockMapUse.Auto, write.BlockMap);
    }

    [Fact]
    public void ValidRestoreRequest_Passes()
    {
        Assert.Empty(EngineRequestValidator.Validate(new RestoreDriveJobRequest { Targets = [Target()], Label = "STICK" }));
    }

    [Fact]
    public void RestoreRequest_WithBadPartsIsRefused()
    {
        var targets = new[] { Target() };

        Assert.NotEmpty(EngineRequestValidator.Validate(new RestoreDriveJobRequest { Targets = [] }));
        Assert.NotEmpty(EngineRequestValidator.Validate(new RestoreDriveJobRequest { Targets = targets, Label = new string('x', 33) }));
        Assert.NotEmpty(EngineRequestValidator.Validate(new RestoreDriveJobRequest { Targets = targets, ClusterSizeBytes = 1000 }));
        Assert.NotEmpty(EngineRequestValidator.Validate(new RestoreDriveJobRequest { Targets = targets, FileSystem = (FileSystemKind)99 }));
        Assert.NotEmpty(EngineRequestValidator.Validate(new RestoreDriveJobRequest
        {
            Targets = [new EngineTarget(TestPaths.Disk3, TestPaths.IdentityOf(TestPaths.Disk4))],
        }));
    }

    [Fact]
    public void ValidVerifyRequest_Passes()
    {
        Assert.Empty(EngineRequestValidator.Validate(new VerifyJobRequest { ImagePath = TestPaths.Image, Targets = [Target()] }));
    }

    [Theory]
    [InlineData("relative.iso", null)]
    [InlineData(@"C:\images\a.iso", "bad\nname")]
    public void VerifyRequest_WithBadPartsIsRefused(string image, string? entry)
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(new VerifyJobRequest { ImagePath = image, Targets = [Target()], ArchiveEntry = entry }));
    }

    [Fact]
    public void ArchiveEntryOfAWriteRequest_IsChecked()
    {
        var request = new RawWriteJobRequest { ImagePath = TestPaths.Image, Targets = [Target()], ArchiveEntry = new string('a', 600) };

        Assert.NotEmpty(EngineRequestValidator.Validate(request));
    }
}
