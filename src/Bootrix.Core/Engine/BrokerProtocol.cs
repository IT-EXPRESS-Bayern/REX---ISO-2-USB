// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Engine;

/// <summary>Method names and message shapes spoken between the GUI and the elevated broker.</summary>
public static class BrokerProtocol
{
    /// <summary>The newest version this build speaks.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The oldest version this build still understands.</summary>
    public const int MinimumVersion = 1;

    public const string Hello = "hello";
    public const string ListDisks = "listDisks";
    public const string CaptureIdentity = "captureIdentity";
    public const string RunJob = "runJob";

    /// <summary>Notification from the broker: progress of a running job.</summary>
    public const string Progress = "progress";

    /// <summary>Notification from the broker: a disk appeared or went away.</summary>
    public const string DevicesChanged = "devicesChanged";

    /// <summary>Notification from the client: it is done, the broker can exit.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>The version both sides will speak, or null when the ranges do not overlap.</summary>
    public static int? Negotiate(int peerMinimum, int peerMaximum)
    {
        var highest = Math.Min(peerMaximum, CurrentVersion);
        var lowest = Math.Max(peerMinimum, MinimumVersion);
        return highest >= lowest ? highest : null;
    }
}

/// <summary>First message of every connection. The secret proves that the client got the launch arguments from the process that started the broker.</summary>
public sealed record HelloParams(int MinVersion, int MaxVersion, string Build, string? Secret);

public sealed record HelloResult(int Version, string Build);

public sealed record ListDisksParams(DiskFilter Filter);

public sealed record CaptureIdentityParams(string DevicePath);

/// <summary>The run id is chosen by the client so that progress notifications can be routed to the right caller before the call has an answer.</summary>
public sealed record RunJobParams(string RunId, EngineJobRequest Request);

public sealed record ProgressNotification(string RunId, ProgressReport Report);
