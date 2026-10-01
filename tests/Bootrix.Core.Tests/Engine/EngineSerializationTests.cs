// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Engine;

public class EngineSerializationTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, IpcJson.Options);
        return JsonSerializer.Deserialize<T>(json, IpcJson.Options)!;
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(0, 5, 1)]
    [InlineData(1, 9, 1)]
    public void Negotiate_PicksTheHighestVersionBothSides(int min, int max, int expected)
    {
        Assert.Equal(expected, BrokerProtocol.Negotiate(min, max));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(-5, 0)]
    [InlineData(5, 1)]
    public void Negotiate_WithoutOverlap_ReturnsNull(int min, int max)
    {
        Assert.Null(BrokerProtocol.Negotiate(min, max));
    }

    [Fact]
    public void StorageDevice_SurvivesTheTrip()
    {
        var device = TestPaths.SampleDevice();

        var copy = RoundTrip(device);

        Assert.Equivalent(device, copy, strict: true);
        Assert.Equal(device.DisplayName, copy.DisplayName);
        Assert.Equal(device.IsBlocked, copy.IsBlocked);
    }

    [Fact]
    public void DeviceThatIsBlocked_StaysBlocked()
    {
        var device = TestPaths.SampleDevice() with { Protection = DeviceProtection.SystemDisk | DeviceProtection.PagefileDisk };

        var copy = RoundTrip(device);

        Assert.True(copy.IsBlocked);
        Assert.Equal(DeviceProtection.SystemDisk | DeviceProtection.PagefileDisk, copy.Protection);
    }

    [Fact]
    public void DiskIdentity_SurvivesTheTripAndStillMatches()
    {
        var identity = TestPaths.IdentityOf(TestPaths.Disk3);

        var copy = RoundTrip(identity);

        Assert.Equal(identity, copy);
        Assert.True(identity.Matches(copy));
    }

    [Fact]
    public void RawWriteRequest_KeepsItsTypeThroughTheBaseClass()
    {
        var holder = new RunJobParams("run1", TestPaths.ValidRequest(TestPaths.Image, TestPaths.Disk3, TestPaths.Disk4) with { Verify = false });

        var copy = RoundTrip(holder);

        var request = Assert.IsType<RawWriteJobRequest>(copy.Request);
        Assert.Equal("run1", copy.RunId);
        Assert.Equal(TestPaths.Image, request.ImagePath);
        Assert.False(request.Verify);
        Assert.Equal(2, request.Targets.Count);
    }

    [Fact]
    public void UnknownRequestType_IsRejectedWhenReading()
    {
        const string json = """{"runId":"x","request":{"type":"format-everything","imagePath":"C:\\a.iso"}}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunJobParams>(json, IpcJson.Options));
    }

    [Fact]
    public void ProgressReport_SurvivesTheTrip()
    {
        var report = new ProgressReport("job-1", 2, 4, "Raw.Write", 0.25, 0.5, 1_000, 4_000, 12_345.5, TimeSpan.FromSeconds(90), "verify");

        Assert.Equal(report, RoundTrip(new ProgressNotification("run1", report)).Report);
    }

    [Fact]
    public void ProgressReport_WithoutEtaAndDetail_SurvivesTheTrip()
    {
        var report = new ProgressReport("job-1", 0, 4, "Raw.CheckTargets", 0, 0, 0, 0, 0, null, null);

        Assert.Equal(report, RoundTrip(new ProgressNotification("run1", report)).Report);
    }

    [Fact]
    public void SucceededResult_HasNoException()
    {
        var result = RoundTrip(new EngineJobResult
        {
            Outcome = JobOutcome.Succeeded,
            Duration = TimeSpan.FromMinutes(3),
            ImageSha256 = "00ff",
            ImageBytes = 123,
        });

        Assert.True(result.Succeeded);
        Assert.Null(result.ToException());
        Assert.Equal("00ff", result.ImageSha256);
        Assert.Equal(TimeSpan.FromMinutes(3), result.Duration);
    }

    [Fact]
    public void CanceledResult_BecomesACancellation()
    {
        var result = RoundTrip(new EngineJobResult { Outcome = JobOutcome.Canceled });

        Assert.IsType<OperationCanceledException>(result.ToException());
    }

    [Fact]
    public void FailedResult_BecomesTheSameErrorTheCatalogDescribesIdentically()
    {
        var original = new BootrixException(ErrorCode.DeviceTooSmall, "technical") { Arguments = ["8 GB", "4 GB"] };
        var result = RoundTrip(EngineJobResult.From(new JobResult(JobOutcome.Failed, TimeSpan.FromSeconds(5), original, "Raw.CheckTargets")));

        var rebuilt = Assert.IsType<BootrixException>(result.ToException());

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal("Raw.CheckTargets", result.FailedStep);
        Assert.Equal(ErrorCode.DeviceTooSmall, rebuilt.Code);
        foreach (var culture in new[] { "de", "en" })
        {
            var localizer = new Localizer { Culture = CultureInfo.GetCultureInfo(culture) };
            Assert.Equal(ErrorCatalog.Describe(original, localizer), ErrorCatalog.Describe(rebuilt, localizer));
        }
    }

    [Fact]
    public void FailedResultWithForeignException_BecomesUnknown()
    {
        var result = RoundTrip(EngineJobResult.From(new JobResult(JobOutcome.Failed, TimeSpan.Zero, new IOException("disk on fire"))));

        var rebuilt = Assert.IsType<BootrixException>(result.ToException());

        Assert.Equal(ErrorCode.Unknown, rebuilt.Code);
        Assert.Contains("disk on fire", rebuilt.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryErrorCode_SurvivesTheWire()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var rebuilt = RpcErrors.FromWire(RoundTrip(RpcErrors.ToWire(new BootrixException(code, "d") { Arguments = [1, "two", null] })));

            if (code == ErrorCode.Canceled)
            {
                Assert.IsType<OperationCanceledException>(rebuilt);
                continue;
            }

            var known = Assert.IsType<BootrixException>(rebuilt);
            Assert.Equal(code, known.Code);
            Assert.Equal(["1", "two", ""], known.Arguments);
        }
    }

    [Fact]
    public void UnknownErrorNumber_FromANewerPeer_BecomesUnknown()
    {
        var rebuilt = Assert.IsType<BootrixException>(RpcErrors.FromWire(new WireError(987654, ["x"], "detail")));

        Assert.Equal(ErrorCode.Unknown, rebuilt.Code);
    }

    [Fact]
    public void NewErrorCodes_HaveBothTexts()
    {
        foreach (var code in new[] { ErrorCode.ElevationDenied, ErrorCode.BrokerStartFailed, ErrorCode.BrokerDisconnected, ErrorCode.BrokerProtocol })
        {
            foreach (var culture in new[] { "de", "en" })
            {
                var description = ErrorCatalog.Describe(
                    new BootrixException(code) { Arguments = ["reason"] },
                    new Localizer { Culture = CultureInfo.GetCultureInfo(culture) });

                Assert.StartsWith("BX8", description.Code, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(description.Cause));
                Assert.False(string.IsNullOrWhiteSpace(description.Action));
            }
        }
    }
}
