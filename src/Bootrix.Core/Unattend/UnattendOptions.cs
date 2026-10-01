// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Unattend;

public sealed record OemBranding
{
    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? SupportProvider { get; init; }

    public string? SupportUrl { get; init; }

    public string? SupportPhone { get; init; }

    public string? SupportHours { get; init; }

    public bool IsEmpty => Manufacturer is null && Model is null && SupportProvider is null && SupportUrl is null
        && SupportPhone is null && SupportHours is null;
}

/// <summary>Everything the answer file needs: the profile's setup options plus values that are only known when the stick is written.</summary>
public sealed record UnattendOptions
{
    public WindowsArch Arch { get; init; } = WindowsArch.X64;

    public WindowsSetupOptions Windows { get; init; } = new();

    /// <summary>Resolved computer name; null leaves it to Setup (random name).</summary>
    public string? ComputerName { get; init; }

    /// <summary>Clear-text password of the local account. It is not part of any profile or log; null creates the account without a password.</summary>
    public string? LocalAccountPassword { get; init; }

    /// <summary>Sign in once automatically after setup (needs a password-less account or a password).</summary>
    public bool AutoLogonOnce { get; init; }

    public string? ProductKey { get; init; }

    /// <summary>Edition to install by its image name, e.g. "Windows 11 Pro"; ignored when <see cref="ImageIndex"/> is set.</summary>
    public string? ImageName { get; init; }

    public int? ImageIndex { get; init; }

    public OemBranding? Branding { get; init; }

    /// <summary>Commands run once after the first sign-in (cmd.exe command lines).</summary>
    public IReadOnlyList<string> FirstLogonCommands { get; init; } = [];

    /// <summary>Commands run in the specialize pass, before any user exists.</summary>
    public IReadOnlyList<string> SpecializeCommands { get; init; } = [];
}
