// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;

namespace Bootrix.Core.Storage.Testing;

public enum StickTestMode
{
    /// <summary>Writes tagged blocks over the claimed range and reads them back: finds sticks with a faked capacity in about a minute.</summary>
    Capacity,

    /// <summary>The capacity check plus two patterns over the whole stick.</summary>
    Quick,

    /// <summary>The capacity check plus four patterns over the whole stick; takes hours on large sticks.</summary>
    Thorough,
}

/// <summary>The outcome for one stick. The stick holds test data afterwards, so it has to be formatted again.</summary>
public sealed record StickTestReport(
    string Device,
    string? Serial,
    StickTestMode Mode,
    CapacityProbeResult Capacity,
    BadBlockResult? BadBlocks)
{
    public bool IsGood => Capacity.IsGenuine && (BadBlocks?.IsClean ?? true);

    public static string Serialize(IReadOnlyList<StickTestReport> reports) => JsonSerializer.Serialize(reports, CoreJson.Options);

    public static IReadOnlyList<StickTestReport> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<StickTestReport>>(json, CoreJson.Options) ?? [];
}
