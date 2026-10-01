// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop;

public sealed record MemoryInfo
{
    /// <summary>Installed RAM according to SMBIOS (GetPhysicallyInstalledSystemMemory); what Windows Setup compares against.</summary>
    public long? InstalledBytes { get; init; }

    /// <summary>RAM Windows can use (GlobalMemoryStatusEx); smaller than the installed amount by what firmware and integrated graphics reserve.</summary>
    public long? UsableBytes { get; init; }
}
