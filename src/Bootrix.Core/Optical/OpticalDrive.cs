// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

/// <summary>A CD/DVD/BD drive. Reading-only drives are listed too, they just have no write capabilities.</summary>
public sealed record OpticalDrive
{
    /// <summary>Opaque identifier of the recorder as handed out by the platform (IMAPI unique ID on Windows).</summary>
    public required string Id { get; init; }

    public string Vendor { get; init; } = "";

    public string Product { get; init; } = "";

    public string Revision { get; init; } = "";

    /// <summary>Drive letter with colon ("G:"), or null when the drive has no volume mounted.</summary>
    public string? DriveLetter { get; init; }

    /// <summary>Number of the CD-ROM device (\\.\CdRomN); the only handle that works for a disc without a recognised file system.</summary>
    public int? DeviceNumber { get; init; }

    public OpticalCapabilities Capabilities { get; init; }

    /// <summary>False for slot-loading drives, which cannot pull a tray in by command.</summary>
    public bool CanLoadMedia { get; init; } = true;

    public string Name => string.Join(' ', new[] { Vendor, Product }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

    public string DisplayName => DriveLetter is null ? Name : $"{DriveLetter} {Name}";

    public bool CanRecord => Capabilities != OpticalCapabilities.None;
}
