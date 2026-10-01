// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings
{
    /// <summary>Culture name such as "de-DE"; empty follows the Windows display language.</summary>
    public string Language { get; init; } = "";

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>List USB hard disks and SSDs, which Windows reports as fixed media.</summary>
    public bool ShowUsbHardDisks { get; init; }

    /// <summary>Service mode: internal disks become selectable. System, boot and pagefile disks stay blocked.</summary>
    public bool ServiceMode { get; init; }

    public bool VerifyAfterWrite { get; init; } = true;

    public bool PlaySoundWhenDone { get; init; } = true;

    public string? LastImageDirectory { get; init; }

    public string? LastDownloadDirectory { get; init; }
}
