// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Workshop;

/// <summary>The Windows license embedded in the firmware (ACPI MSDM table).</summary>
public sealed record OemLicenseInfo
{
    public bool HasFirmwareKey { get; init; }

    /// <summary>Safe to show and log: all but the last group is replaced by X.</summary>
    public string? MaskedKey { get; init; }

    /// <summary>The key in clear text. Only for the encrypted customer sheet; never log or print it.</summary>
    [Sensitive]
    public string? PlainKey { get; init; }

    /// <summary>Raw text of OA3xOriginalProductKeyDescription, kept because its format is not documented.</summary>
    public string? EditionDescription { get; init; }

    /// <summary>Edition ID such as Professional or Core when the description could be understood.</summary>
    public string? EditionId { get; init; }

    /// <summary>License channel from the description, usually OEM.</summary>
    public string? Channel { get; init; }

    public string? TableOemId { get; init; }

    // The generated ToString would print PlainKey.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HasFirmwareKey = ").Append(HasFirmwareKey)
            .Append(", MaskedKey = ").Append(MaskedKey)
            .Append(", EditionDescription = ").Append(EditionDescription)
            .Append(", EditionId = ").Append(EditionId)
            .Append(", Channel = ").Append(Channel)
            .Append(", TableOemId = ").Append(TableOemId);
        return true;
    }
}
