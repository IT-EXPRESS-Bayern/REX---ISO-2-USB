// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Bootrix.Core.Net;

/// <summary>
/// A set of OpenPGP public keys shipped with Bootrix (or with a signed catalog). Never filled from a key server:
/// which key may vouch for which download is decided by whoever ships the keyring.
/// </summary>
public sealed partial class OpenPgpKeyring
{
    private readonly List<PgpPublicKeyRing> _rings;

    private OpenPgpKeyring(List<PgpPublicKeyRing> rings)
    {
        _rings = rings;
        Fingerprints = [.. rings.Select(r => Convert.ToHexString(r.GetPublicKey().GetFingerprint()))];
    }

    /// <summary>Fingerprints of the primary keys, upper-case hex.</summary>
    public IReadOnlyList<string> Fingerprints { get; }

    /// <summary>
    /// Reads a binary keyring (<c>gpg --export</c>, Debian's <c>.gpg</c> files) or one or more ASCII-armored
    /// public key blocks (<c>.asc</c>).
    /// </summary>
    public static OpenPgpKeyring Load(ReadOnlySpan<byte> data)
    {
        var rings = new List<PgpPublicKeyRing>();

        try
        {
            if (LooksArmored(data))
            {
                foreach (Match block in ArmorBlock().Matches(Encoding.ASCII.GetString(data)))
                {
                    rings.AddRange(ReadRings(Encoding.ASCII.GetBytes(block.Value)));
                }
            }
            else
            {
                rings.AddRange(ReadRings(data.ToArray()));
            }
        }
        catch (Exception ex) when (ex is PgpException or IOException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("The OpenPGP keyring cannot be read: " + ex.Message, ex);
        }

        return rings.Count > 0 ? new OpenPgpKeyring(rings) : throw new InvalidDataException("The OpenPGP keyring contains no public key.");
    }

    /// <summary>Every key (primary or subkey) with this ID, each with its primary key. A key may be present more than once.</summary>
    internal IEnumerable<(PgpPublicKey Key, PgpPublicKey Primary)> Find(long keyId)
    {
        foreach (var ring in _rings)
        {
            if (ring.GetPublicKey(keyId) is { } key)
            {
                yield return (key, ring.GetPublicKey());
            }
        }
    }

    private static List<PgpPublicKeyRing> ReadRings(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var decoder = PgpUtilities.GetDecoderStream(input);
        var factory = new PgpObjectFactory(decoder);

        var rings = new List<PgpPublicKeyRing>();
        for (var next = factory.NextPgpObject(); next is not null; next = factory.NextPgpObject())
        {
            if (next is PgpPublicKeyRing ring)
            {
                rings.Add(ring);
            }
        }

        return rings;
    }

    private static bool LooksArmored(ReadOnlySpan<byte> data) =>
        Encoding.ASCII.GetString(data[..Math.Min(data.Length, 64)]).TrimStart().StartsWith("-----BEGIN PGP", StringComparison.Ordinal);

    [GeneratedRegex(@"-----BEGIN PGP PUBLIC KEY BLOCK-----.*?-----END PGP PUBLIC KEY BLOCK-----", RegexOptions.Singleline)]
    private static partial Regex ArmorBlock();
}
