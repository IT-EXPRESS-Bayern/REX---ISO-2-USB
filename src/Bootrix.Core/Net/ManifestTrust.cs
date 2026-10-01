// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>
/// The public keys that signed catalog updates must carry. The list stays empty until the project's
/// signing keys exist; with no trusted key no update is ever accepted and Bootrix keeps using the
/// catalogs that ship inside the program. Never put a private key anywhere near this file.
/// </summary>
public static class ManifestTrust
{
    public static IReadOnlyList<ManifestPublicKey> TrustedKeys { get; } = [];
}
