// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Workshop.Advice;

public sealed record BootRecommendation
{
    public TargetFirmware Firmware { get; init; } = TargetFirmware.Auto;

    public PartitionScheme Scheme { get; init; } = PartitionScheme.Auto;

    public FileSystemKind FileSystem { get; init; } = FileSystemKind.Auto;

    public bool? SecureBootEnabled { get; init; }

    /// <summary>With Secure Boot on, only media whose boot manager the firmware trusts will start.</summary>
    public bool? RequiresSignedMedia { get; init; }

    public BootCertificate Certificate { get; init; } = BootCertificate.Auto;

    public IReadOnlyList<AdvisorMessage> Notes { get; init; } = [];
}
