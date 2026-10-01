// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Json;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Tests.Engine;

public class EngineRequestSerializationTests
{
    private static T RoundTrip<T>(T value) where T : EngineJobRequest
    {
        var json = JsonSerializer.Serialize<EngineJobRequest>(value, CoreJson.Options);
        return (T)JsonSerializer.Deserialize<EngineJobRequest>(json, CoreJson.Options)!;
    }

    [Fact]
    public void RawWriteRequestSurvivesTheTripWithItsIdentity()
    {
        var identity = new DiskIdentity { DevicePath = @"\\?\usbstor#disk&ven_x", Serial = "S1", SizeBytes = 16_000_000_000, TableHash = "ab12" };
        var request = new RawWriteJobRequest { ImagePath = @"C:\iso\a.iso", Targets = [new EngineTarget(identity.DevicePath, identity)], Verify = false };

        var copy = RoundTrip(request);

        Assert.Equal(request.ImagePath, copy.ImagePath);
        Assert.False(copy.Verify);
        var target = Assert.Single(copy.Targets);
        Assert.True(identity.Matches(target.Identity));
        Assert.Equal("ab12", target.Identity.TableHash);
    }

    [Fact]
    public void TinyRequestKeepsGroupsCompressionAndUnattend()
    {
        var request = new TinyBuildJobRequest
        {
            IsoPath = @"C:\iso\win11.iso",
            OutputIsoPath = @"C:\out\tiny.iso",
            ProfileId = "tiny11core",
            Edition = "Pro",
            KeepGroups = ["edge"],
            IncludeGroups = ["tools"],
            Compression = InstallImageCompression.Maximum,
            AcknowledgeNoServicing = true,
            Unattend = new UnattendOptions { LocalAccountPassword = "geheim", Windows = new WindowsSetupOptions { LocalAccountName = "Kunde", BypassTpm = true } },
        };

        var copy = RoundTrip(request);

        Assert.Equal(["edge"], copy.KeepGroups);
        Assert.Equal(["tools"], copy.IncludeGroups);
        Assert.Equal(InstallImageCompression.Maximum, copy.Compression);
        Assert.True(copy.AcknowledgeNoServicing);
        Assert.Equal("Kunde", copy.Unattend!.Windows.LocalAccountName);
        Assert.Equal("geheim", copy.Unattend.LocalAccountPassword);
    }

    [Fact]
    public void ADocumentWithoutTheDefaultedFieldsStillGetsTheDefaults()
    {
        const string json = """{ "type": "tiny-build", "isoPath": "a.iso", "outputIsoPath": "b.iso" }""";

        var request = Assert.IsType<TinyBuildJobRequest>(JsonSerializer.Deserialize<EngineJobRequest>(json, CoreJson.Options));

        Assert.Equal("tiny11", request.ProfileId);
        Assert.True(request.BypassHardwareChecks);
        Assert.Equal(InstallImageCompression.Recovery, request.Compression);
        Assert.Empty(request.KeepGroups);
    }

    [Fact]
    public void UnknownTypeIsRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EngineJobRequest>("""{ "type": "format-c" }""", CoreJson.Options));
    }

    [Fact]
    public void ResultKeepsTheErrorCodeAndArguments()
    {
        var failure = new JobResult(JobOutcome.Failed, TimeSpan.FromSeconds(5), new BootrixException(ErrorCode.DeviceTooSmall, "x") { Arguments = ["4 GB", "1 GB"] }, "Raw.CheckTargets");

        var copy = JsonSerializer.Deserialize<EngineJobResult>(JsonSerializer.Serialize(EngineJobResult.From(failure), CoreJson.Options), CoreJson.Options)!;

        Assert.False(copy.Succeeded);
        var exception = Assert.IsType<BootrixException>(copy.ToException());
        Assert.Equal(ErrorCode.DeviceTooSmall, exception.Code);
        Assert.Equal(["4 GB", "1 GB"], exception.Arguments);
        Assert.Equal("Raw.CheckTargets", copy.FailedStep);
    }

    [Fact]
    public void CanceledResultBecomesACancellation()
    {
        var result = EngineJobResult.From(new JobResult(JobOutcome.Canceled, TimeSpan.Zero, new OperationCanceledException()));

        Assert.IsType<OperationCanceledException>(result.ToException());
    }

    [Fact]
    public void SuccessHasNoException()
    {
        Assert.Null(EngineJobResult.From(new JobResult(JobOutcome.Succeeded, TimeSpan.Zero)).ToException());
    }

    [Fact]
    public void WriteImageRequestKeepsItsSourceAndSpec()
    {
        var identity = new DiskIdentity { DevicePath = @"\\?\usbstor#disk", SizeBytes = 1 };
        var request = new WriteImageJobRequest
        {
            Source = WriteSource.Dos,
            Targets = [new EngineTarget(identity.DevicePath, identity)],
            Spec = new JobSpec { Target = new TargetOptions { LegacyBiosFixes = true, Label = "DOS" } },
        };

        var copy = RoundTrip(request);

        Assert.Equal(WriteSource.Dos, copy.Source);
        Assert.Null(copy.ImagePath);
        Assert.True(copy.Spec.Target.LegacyBiosFixes);
        Assert.Equal("DOS", copy.Spec.Target.Label);
    }

    [Theory]
    [InlineData(WriteSource.Image, null, false)]
    [InlineData(WriteSource.Image, @"C:\iso\a.iso", true)]
    [InlineData(WriteSource.Dos, null, true)]
    [InlineData(WriteSource.Format, null, true)]
    [InlineData(WriteSource.Dos, @"C:\iso\a.iso", false)]
    public void TheValidatorOnlyWantsAnImagePathForImages(WriteSource source, string? path, bool valid)
    {
        var identity = new DiskIdentity { DevicePath = @"\\?\usbstor#disk&ven_x#1#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}", SizeBytes = 1 };
        var request = new WriteImageJobRequest { Source = source, ImagePath = path, Targets = [new EngineTarget(identity.DevicePath, identity)] };

        Assert.Equal(valid, EngineRequestValidator.Validate(request).Count == 0);
    }
}
