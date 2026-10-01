// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Workshop.Advice;

namespace Bootrix.Core.Workshop.Capture;

public sealed record InstalledProgram(string Name, string? Version, string? Publisher);

/// <summary>A driver package that someone other than Windows put into the driver store (oemNN.inf).</summary>
public sealed record ThirdPartyDriver
{
    public required string PublishedName { get; init; }

    public string? Provider { get; init; }

    public string? ClassName { get; init; }

    public string? Version { get; init; }

    public DateOnly? Date { get; init; }

    public string? CatalogFile { get; init; }
}

public sealed record WlanProfile
{
    public required string Name { get; init; }

    public string? Authentication { get; init; }

    public string? Encryption { get; init; }

    public string? ConnectionMode { get; init; }

    /// <summary>The profile has a pre-shared key but it came out encrypted, which is what an export without administrator rights yields.</summary>
    public bool? KeyProtected { get; init; }

    /// <summary>The network key in clear text, only after the user opted into the export. Never log or print it.</summary>
    [Sensitive]
    public string? Key { get; init; }

    /// <summary>The XML file of the opt-in export. It contains the key in clear text as well.</summary>
    public string? ExportedFile { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name)
            .Append(", Authentication = ").Append(Authentication)
            .Append(", Encryption = ").Append(Encryption)
            .Append(", ConnectionMode = ").Append(ConnectionMode)
            .Append(", KeyProtected = ").Append(KeyProtected)
            .Append(", ExportedFile = ").Append(ExportedFile);
        return true;
    }
}

public sealed record BitLockerRecoveryKey
{
    /// <summary>Drive letter with colon.</summary>
    public required string Volume { get; init; }

    /// <summary>GUID of the key protector; the lock screen shows its first eight characters as the key ID.</summary>
    public required string ProtectorId { get; init; }

    /// <summary>The 48-digit recovery password. Only on the customer sheet; never log or print it elsewhere.</summary>
    [Sensitive]
    public string? RecoveryPassword { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Volume = ").Append(Volume).Append(", ProtectorId = ").Append(ProtectorId);
        return true;
    }
}

public sealed record WindowsProductKeyInfo
{
    public string? MaskedKey { get; init; }

    [Sensitive]
    public string? PlainKey { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("MaskedKey = ").Append(MaskedKey);
        return true;
    }
}

/// <summary>
/// Inventory of a customer PC taken before it is reinstalled. Lists are null when their source was not requested or failed.
/// The secrets in it (<see cref="SensitiveAttribute"/>) appear only after the matching opt-in in <see cref="CustomerPcCaptureOptions"/>.
/// </summary>
public sealed record CustomerPcCapture
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset? CapturedAt { get; init; }

    public IReadOnlyList<WlanProfile>? WlanProfiles { get; init; }

    public IReadOnlyList<InstalledProgram>? InstalledPrograms { get; init; }

    public IReadOnlyList<ThirdPartyDriver>? ThirdPartyDrivers { get; init; }

    public IReadOnlyList<BitLockerRecoveryKey>? BitLockerRecoveryKeys { get; init; }

    public WindowsProductKeyInfo? WindowsProductKey { get; init; }

    public IReadOnlyList<AdvisorMessage> Notes { get; init; } = [];

    public IReadOnlyList<CollectionIssue> Issues { get; init; } = [];

    /// <summary>True when any clear-text secret is present; the sheet must then be stored encrypted.</summary>
    public bool ContainsSecrets =>
        WindowsProductKey?.PlainKey is not null
        || (WlanProfiles?.Any(p => p.Key is not null) ?? false)
        || (BitLockerRecoveryKeys?.Any(k => k.RecoveryPassword is not null) ?? false);
}
