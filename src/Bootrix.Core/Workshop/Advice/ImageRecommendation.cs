// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop.Advice;

public enum EditionSource
{
    None,

    /// <summary>The Windows key embedded in the firmware names the edition.</summary>
    FirmwareKey,

    /// <summary>No firmware key: the edition of the running Windows is suggested.</summary>
    RunningSystem,
}

public sealed record ImageRecommendation
{
    public required WindowsProduct Product { get; init; }

    public required string Version { get; init; }

    public required CpuArchitecture Architecture { get; init; }

    /// <summary>Image name inside install.wim, e.g. "Windows 11 Pro"; null when no edition could be determined.</summary>
    public string? Edition { get; init; }

    /// <summary>Edition ID such as Professional, for ei.cfg or a catalog filter.</summary>
    public string? EditionId { get; init; }

    public EditionSource EditionSource { get; init; }

    /// <summary>License channel of the firmware key (OEM or Retail) when known; belongs into ei.cfg.</summary>
    public string? Channel { get; init; }

    /// <summary>UI language of the running Windows as BCP 47 tag; null in Windows PE, where it says nothing about the installation.</summary>
    public string? Language { get; init; }

    /// <summary>Catalog query such as "windows11/25H2/de-DE/x64"; null while the language is unknown.</summary>
    public string? CatalogQuery { get; init; }

    public DateOnly? EndOfServicing { get; init; }

    /// <summary>Other ways forward, ordered by preference.</summary>
    public IReadOnlyList<AdvisorMessage> Alternatives { get; init; } = [];
}
