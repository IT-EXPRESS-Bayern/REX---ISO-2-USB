// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Advice;

namespace Bootrix.Core.Workshop;

/// <summary>What `bootrix-cli inspect --json` prints: the measurements and the advice derived from them.</summary>
public sealed record TargetPcReport
{
    public required TargetPcInfo Info { get; init; }

    public required TargetPcAssessment Assessment { get; init; }

    public static TargetPcReport Create(TargetPcInfo info, TargetPcAdvisorOptions? options = null) => new()
    {
        Info = info,
        Assessment = TargetPcAdvisor.Evaluate(info, options),
    };

    /// <summary>JSON without any <see cref="SensitiveAttribute"/> property.</summary>
    public string ToJson() => WorkshopJson.Serialize(this);
}
