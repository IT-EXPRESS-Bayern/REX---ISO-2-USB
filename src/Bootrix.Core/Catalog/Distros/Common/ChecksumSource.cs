// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>
/// Loads a vendor checksum file. The signed variants return it only if the OpenPGP signature is valid and made by a
/// pinned key; anything else throws <see cref="Errors.ErrorCode.SignatureInvalid"/>. There is no quiet fallback to
/// an unsigned reading: a distribution that is signed stays signed.
/// </summary>
internal static class ChecksumSource
{
    /// <summary>
    /// Signed documents are fetched fresh, never from the cache a listing filled: a file and a signature taken from
    /// different moments would not match when the vendor republished in between.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.Zero;

    public static async Task<ChecksumFile> DetachedAsync(
        DistroHttp http,
        Uri sums,
        Uri signature,
        PinnedKeys keys,
        CancellationToken cancellationToken)
    {
        var data = await http.GetBytesAsync(sums, cancellationToken, Fresh).ConfigureAwait(false);
        var signatureBytes = await http.GetBytesAsync(signature, cancellationToken, Fresh).ConfigureAwait(false);

        OpenPgpVerifier.Verify(data, signatureBytes, keys.Keyring, keys.Fingerprints, http.Time).ThrowIfNotValid(sums.AbsoluteUri);
        return ChecksumFile.Parse(data);
    }

    public static async Task<ChecksumFile> ClearSignedAsync(
        DistroHttp http,
        Uri document,
        PinnedKeys keys,
        CancellationToken cancellationToken)
    {
        var data = await http.GetBytesAsync(document, cancellationToken, Fresh).ConfigureAwait(false);

        var verified = OpenPgpVerifier.VerifyClearSigned(data, keys.Keyring, keys.Fingerprints, http.Time);
        verified.Result.ThrowIfNotValid(document.AbsoluteUri);

        // Only the signed text counts; the armor around it is not data.
        return ChecksumFile.Parse(verified.Text);
    }

    public static async Task<ChecksumFile> UnsignedAsync(DistroHttp http, Uri sums, CancellationToken cancellationToken) =>
        ChecksumFile.Parse(await http.GetBytesAsync(sums, cancellationToken).ConfigureAwait(false));

    /// <summary>SHA-256 if the file has it, otherwise the strongest digest it offers.</summary>
    public static FileHash Pick(this ChecksumFile file, string fileName) =>
        file.TryGetHash(fileName, HashKind.Sha256, out var sha256) ? sha256! : file.Select(fileName);
}
