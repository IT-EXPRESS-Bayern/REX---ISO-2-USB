// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

public enum BootSupport
{
    /// <summary>The vendor does not say and it could not be established otherwise.</summary>
    Unknown,
    No,
    Yes,
}

/// <summary>Which firmware start-up paths a medium is known to work with.</summary>
public readonly record struct RescueFirmware(BootSupport Bios, BootSupport Uefi, BootSupport SecureBoot);
