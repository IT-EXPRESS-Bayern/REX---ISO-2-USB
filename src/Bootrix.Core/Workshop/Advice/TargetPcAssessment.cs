// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Workshop.Advice;

/// <summary>The result of <see cref="TargetPcAdvisor.Evaluate"/>: what the PC can run, what the media must bring, and a job to start from.</summary>
public sealed record TargetPcAssessment
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The date the lifecycle statements refer to.</summary>
    public DateOnly AssessedOn { get; init; }

    public Windows11Verdict Windows11 { get; init; }

    public IReadOnlyList<RequirementCheck> Windows11Checks { get; init; } = [];

    /// <summary>LabConfig checks that would fail. Only a suggestion: bypassing them leaves Windows 11 unsupported by Microsoft.</summary>
    public IReadOnlyList<LabConfigBypass> Bypasses { get; init; } = [];

    /// <summary>Null when no Windows installation medium fits the hardware.</summary>
    public ImageRecommendation? Image { get; init; }

    public IReadOnlyList<DriverNeed> DriverNeeds { get; init; } = [];

    public BootRecommendation Boot { get; init; } = new();

    public IReadOnlyList<AdvisorMessage> Messages { get; init; } = [];

    /// <summary>
    /// A write job prefilled with what the check derived: image query, partition scheme, firmware, edition, language,
    /// time zone and bypasses. Everything else keeps its default; the user still chooses the target and confirms.
    /// </summary>
    public JobSpec SuggestedProfile { get; init; } = new();
}
