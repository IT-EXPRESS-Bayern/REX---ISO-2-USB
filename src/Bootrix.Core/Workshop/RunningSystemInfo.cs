// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop;

/// <summary>The Windows that is running now; used to preselect language, time zone and edition for the new installation.</summary>
public sealed record RunningSystemInfo
{
    /// <summary>First build number of Windows 11; the registry still calls it "Windows 10".</summary>
    public const int Windows11FirstBuild = 22000;

    public string? ProductName { get; init; }

    /// <summary>EditionID from the registry, e.g. Professional, Core or ServerStandard.</summary>
    public string? EditionId { get; init; }

    public int? BuildNumber { get; init; }

    /// <summary>Feature update name such as 24H2.</summary>
    public string? DisplayVersion { get; init; }

    /// <summary>Architecture of the installed Windows.</summary>
    public CpuArchitecture Architecture { get; init; }

    public bool? IsServer { get; init; }

    /// <summary>The system runs from a Windows PE image; language and time zone then do not describe the customer's installation.</summary>
    public bool? IsWinPe { get; init; }

    /// <summary>Installed UI language as BCP 47 tag, e.g. de-DE.</summary>
    public string? UiLanguage { get; init; }

    /// <summary>Windows time zone ID, e.g. "W. Europe Standard Time".</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Input locales in the form used by unattend files, e.g. "0407:00000407".</summary>
    public IReadOnlyList<string> KeyboardLayouts { get; init; } = [];

    public bool? IsWindows11 => BuildNumber is null || IsServer == true ? null : BuildNumber >= Windows11FirstBuild;
}
