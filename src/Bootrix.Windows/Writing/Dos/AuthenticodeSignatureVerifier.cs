// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Windows.Platform;

namespace Bootrix.Windows.Writing.Dos;

/// <summary>Lets the Core download logic ask Windows whether a file carries a valid signature of Microsoft.</summary>
internal sealed class AuthenticodeSignatureVerifier : IMicrosoftSignatureVerifier
{
    public bool IsSignedByMicrosoft(string path) => AuthenticodeVerifier.IsSignedByMicrosoft(path);
}
