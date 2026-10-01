// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Net;

public enum OpenPgpStatus
{
    /// <summary>The signature is correct and made by a key that is in the keyring, valid and, if pinned, allowed.</summary>
    Valid,

    /// <summary>The signing key is not in the keyring.</summary>
    KeyUnknown,

    /// <summary>The signature does not match the data, is malformed, or uses a weak algorithm.</summary>
    Invalid,

    KeyExpired,

    KeyRevoked,

    /// <summary>The key is in the keyring but its fingerprint is not among the pinned ones.</summary>
    KeyNotAllowed,
}

/// <param name="Fingerprint">Fingerprint of the signing key's primary key, upper-case hex; null if the key is unknown.</param>
/// <param name="KeyId">Issuer key ID from the signature, 16 hex digits.</param>
/// <param name="Reason">What was wrong, for the log. Null when the signature is valid.</param>
public sealed record OpenPgpResult(OpenPgpStatus Status, string? Fingerprint, string? KeyId, string? Reason = null)
{
    public bool IsValid => Status == OpenPgpStatus.Valid;

    /// <summary>Throws <see cref="ErrorCode.SignatureInvalid"/> unless the signature is valid.</summary>
    public void ThrowIfNotValid(string subject)
    {
        if (!IsValid)
        {
            throw new BootrixException(ErrorCode.SignatureInvalid, $"{subject}: {Status}{(Reason is null ? string.Empty : " - " + Reason)}");
        }
    }
}

/// <param name="Text">The signed text with the clear-signing armor removed, lines separated by LF. Only this part is covered by the signature.</param>
public sealed record ClearSignedResult(OpenPgpResult Result, string Text);
