// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>
/// A distribution's public signing keys as shipped inside Bootrix, together with the fingerprints that may vouch for
/// its downloads. The fingerprints are written down in code (and compared with the vendor's web page in the tests),
/// so swapping the key file alone cannot make another key acceptable.
/// </summary>
internal sealed class PinnedKeys
{
    private const string ResourcePrefix = "Bootrix.Core.Catalog.Distros.Keys.";

    private PinnedKeys(OpenPgpKeyring keyring, IReadOnlyList<string> fingerprints)
    {
        Keyring = keyring;
        Fingerprints = fingerprints;
    }

    public OpenPgpKeyring Keyring { get; }

    /// <summary>Upper-case hex, no spaces.</summary>
    public IReadOnlyList<string> Fingerprints { get; }

    /// <summary>The same keyring, but only <paramref name="fingerprint"/> may vouch.</summary>
    public PinnedKeys Restrict(string fingerprint) => new(Keyring, [fingerprint]);

    public static PinnedKeys FromResource(string fileName, params string[] fingerprints)
    {
        using var stream = typeof(PinnedKeys).Assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"Signing key resource '{fileName}' is missing.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var keyring = OpenPgpKeyring.Load(buffer.ToArray());

        var missing = fingerprints.Where(f => !keyring.Fingerprints.Contains(f, StringComparer.Ordinal)).ToList();
        return missing.Count == 0
            ? new PinnedKeys(keyring, fingerprints)
            : throw new InvalidOperationException($"'{fileName}' does not contain the pinned key(s) {string.Join(", ", missing)}.");
    }
}
