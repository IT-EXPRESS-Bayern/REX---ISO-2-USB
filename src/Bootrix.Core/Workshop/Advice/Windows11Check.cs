// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Advice;

/// <summary>The minimum requirements Microsoft lists for Windows 11, each judged on its own.</summary>
public enum Windows11Requirement
{
    Architecture,
    Cores,
    Clock,
    CpuModel,
    Ram,
    Storage,
    Firmware,
    SecureBoot,
    Tpm,
    Graphics,
}

public enum CheckStatus
{
    Pass,
    Fail,

    /// <summary>A measurement is missing or too close to the limit to call.</summary>
    Unknown,

    /// <summary>Bootrix does not measure this requirement at all.</summary>
    NotChecked,
}

/// <summary>A check Windows 11 Setup performs and that can be switched off with a LabConfig value in the boot image.</summary>
public enum LabConfigBypass
{
    Tpm,
    SecureBoot,
    Ram,
    Cpu,
    Storage,
}

public enum Windows11Verdict
{
    Unknown,

    /// <summary>Every measured minimum is met. Microsoft's list of supported processors and DirectX 12 are not compared.</summary>
    LikelySupported,

    /// <summary>The PC misses requirements that Setup lets one bypass; the installation is then unsupported by Microsoft.</summary>
    NeedsBypass,

    /// <summary>A requirement that no bypass can fix is missing, such as a 64-bit processor.</summary>
    NotPossible,
}

public sealed record RequirementCheck(Windows11Requirement Requirement, CheckStatus Status, AdvisorMessage Message, LabConfigBypass? Bypass = null);
