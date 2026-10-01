// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop;

public enum BitLockerProtection
{
    Unknown,
    Off,
    On,
}

public enum BitLockerConversion
{
    Unknown,
    FullyDecrypted,
    FullyEncrypted,
    EncryptionInProgress,
    DecryptionInProgress,
    EncryptionPaused,
    DecryptionPaused,
}

public sealed record BitLockerVolumeInfo
{
    /// <summary>Drive letter with colon, e.g. "C:".</summary>
    public required string Volume { get; init; }

    public BitLockerProtection Protection { get; init; }

    public BitLockerConversion Conversion { get; init; }

    public bool? IsLocked { get; init; }

    public string? EncryptionMethod { get; init; }

    /// <summary>The volume holds encrypted data now or is being converted, whether or not protection is currently suspended.</summary>
    public bool IsEncryptedOrConverting =>
        Protection == BitLockerProtection.On
        || Conversion is not (BitLockerConversion.Unknown or BitLockerConversion.FullyDecrypted);
}
