// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot;

/// <summary>Where a piece of revocation data came from, so reports can say how current they are.</summary>
public sealed record RevocationSource(string Repository, string Ref, string Commit, string File, string Sha256, string? License);

/// <param name="Hash">Authenticode SHA-256 as upper-case hex.</param>
public sealed record RevokedImage(string Hash, EfiMachine Machine, string FileName, string Company, DateOnly? Added, string? Description);

public sealed record RevokedCertificate(string Subject, string Sha1Thumbprint, DateOnly? Added, string? Description);

/// <summary>A DBX entry that raises the minimum Secure Version Number of one Microsoft boot component.</summary>
public sealed record SvnRequirement(Guid Component, string FileName, SecurityVersion Minimum, DateOnly? Changed, string? Description);

/// <summary>
/// Everything the analyzer knows about revoked boot binaries: DBX image hashes, revoked certificates,
/// Microsoft SVN minimums and the SBAT level. The embedded snapshot is the default; a DBX read from a device
/// can be turned into the same structure with <see cref="FromDbx"/> and combined with <see cref="Merge"/>.
/// </summary>
public sealed class RevocationData
{
    /// <summary>EFI_BOOTMGR_DBXSVN_GUID: the SVN entry that belongs to bootmgfw.efi.</summary>
    public static readonly Guid BootmgrSvnGuid = new("9d132b61-59d5-4388-ab1c-185c3cb2eb92");

    /// <summary>Owner GUID of every SVN entry in the DBX; the entries are EFI_CERT_SHA256_GUID lists.</summary>
    private static readonly Guid SvnOwnerGuid = new("9d132b6c-59d5-4388-ab1c-185cfcb2eb92");

    private static readonly Dictionary<Guid, string> SvnFileNames = new()
    {
        [BootmgrSvnGuid] = "bootmgfw.efi",
        [new Guid("e8f82e9d-e127-4158-a488-4c18abe2f284")] = "cdboot.efi",
        [new Guid("c999cac2-7ffe-496f-8127-9e2a8a535976")] = "wdsmgfw.efi",
    };

    private static readonly Lazy<RevocationData> EmbeddedSnapshot = new(RevocationSnapshotReader.ReadEmbedded);

    private readonly Dictionary<string, RevokedImage> _images;
    private readonly Dictionary<string, RevokedCertificate> _certificates;
    private readonly HashSet<string> _revokedTbsHashes;

    internal RevocationData(
        DateOnly? retrieved,
        IReadOnlyList<RevocationSource> sources,
        IEnumerable<RevokedImage> images,
        IEnumerable<RevokedCertificate> certificates,
        IEnumerable<string> revokedTbsHashes,
        IEnumerable<SvnRequirement> svns,
        SbatLevel? sbatLevel,
        IReadOnlyList<byte[]> trustedCertificates)
    {
        Retrieved = retrieved;
        Sources = sources;
        SbatLevel = sbatLevel;
        TrustedCertificates = trustedCertificates;

        _images = new Dictionary<string, RevokedImage>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            _images.TryAdd(image.Hash, image);
        }

        _certificates = new Dictionary<string, RevokedCertificate>(StringComparer.Ordinal);
        foreach (var certificate in certificates)
        {
            _certificates.TryAdd(certificate.Sha1Thumbprint, certificate);
        }

        _revokedTbsHashes = new HashSet<string>(revokedTbsHashes, StringComparer.Ordinal);

        // Several updates can raise the same component; only the highest minimum is relevant.
        Svns = svns
            .GroupBy(s => s.Component)
            .Select(g => g.MaxBy(s => s.Minimum)!)
            .ToList();
    }

    /// <summary>The snapshot compiled into Bootrix (see tools/update-revocations.sh).</summary>
    public static RevocationData Embedded => EmbeddedSnapshot.Value;

    public DateOnly? Retrieved { get; }

    public IReadOnlyList<RevocationSource> Sources { get; }

    public IReadOnlyCollection<RevokedImage> Images => _images.Values;

    public IReadOnlyCollection<RevokedCertificate> Certificates => _certificates.Values;

    public IReadOnlyList<SvnRequirement> Svns { get; }

    /// <summary>The minimum SBAT generations; null for data that does not carry them, such as a plain DBX.</summary>
    public SbatLevel? SbatLevel { get; }

    /// <summary>DER encoded CA certificates delivered with the snapshot; callers must verify them against known thumbprints.</summary>
    public IReadOnlyList<byte[]> TrustedCertificates { get; }

    public static RevocationData Load(Stream json) => RevocationSnapshotReader.Read(json);

    /// <summary>
    /// Builds revocation data from the content of a dbx variable or a signed DBX update file. SHA-256 entries become
    /// image hashes, except those owned by the SVN GUID, which are Microsoft's version minimums; X.509 entries become
    /// revoked certificates and EFI_CERT_X509_SHA256 entries revoke a certificate by its TBS hash.
    /// </summary>
    public static RevocationData FromDbx(ReadOnlyMemory<byte> dbx)
    {
        var images = new List<RevokedImage>();
        var certificates = new List<RevokedCertificate>();
        var tbsHashes = new List<string>();
        var svns = new List<SvnRequirement>();

        foreach (var list in EfiSignatureDatabase.Parse(dbx))
        {
            foreach (var entry in list.Entries)
            {
                var data = entry.Data.Span;
                switch (list.Type)
                {
                    case EfiSignatureType.Sha256 when entry.Owner == SvnOwnerGuid:
                        if (TryReadSvn(data, out var svn))
                        {
                            svns.Add(svn);
                        }

                        break;
                    case EfiSignatureType.Sha256 when data.Length == 32:
                        images.Add(new RevokedImage(Convert.ToHexString(data), EfiMachine.Unknown, string.Empty, string.Empty, null, null));
                        break;
                    case EfiSignatureType.X509:
                        certificates.Add(DescribeCertificate(entry.Data.ToArray()));
                        break;
                    case EfiSignatureType.X509Sha256 when data.Length >= 32:
                        tbsHashes.Add(Convert.ToHexStringLower(data[..32]));
                        break;
                }
            }
        }

        return new RevocationData(null, [], images, certificates, tbsHashes, svns, null, []);
    }

    public RevocationData Merge(RevocationData other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new RevocationData(
            Retrieved is { } mine && other.Retrieved is { } theirs ? (mine > theirs ? mine : theirs) : Retrieved ?? other.Retrieved,
            [.. Sources, .. other.Sources],
            _images.Values.Concat(other._images.Values),
            _certificates.Values.Concat(other._certificates.Values),
            _revokedTbsHashes.Concat(other._revokedTbsHashes),
            Svns.Concat(other.Svns),
            other.SbatLevel ?? SbatLevel,
            [.. TrustedCertificates, .. other.TrustedCertificates]);
    }

    public RevokedImage? FindImage(string authenticodeSha256) =>
        _images.GetValueOrDefault(authenticodeSha256.ToUpperInvariant());

    /// <summary>The first certificate of the chain that is revoked, matched by SHA-1 thumbprint or TBS hash.</summary>
    public EfiCertificateInfo? FindRevokedCertificate(IEnumerable<EfiCertificateInfo> chain) =>
        chain.FirstOrDefault(c => _certificates.ContainsKey(c.Sha1Thumbprint) || _revokedTbsHashes.Contains(c.TbsSha256Thumbprint));

    public SvnRequirement? FindSvn(Guid component) => Svns.FirstOrDefault(s => s.Component == component);

    private static bool TryReadSvn(ReadOnlySpan<byte> data, out SvnRequirement requirement)
    {
        requirement = null!;

        // SVN_DATA: version byte 1, application GUID, minor and major as UINT16, 11 bytes of zero padding.
        if (data.Length != 32 || data[0] != 1 || data[21..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        var minor = BinaryPrimitives.ReadUInt16LittleEndian(data[17..]);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(data[19..]);
        var component = new Guid(data[1..17]);
        requirement = new SvnRequirement(component, SvnFileNames.GetValueOrDefault(component, string.Empty), new SecurityVersion(major, minor), null, null);
        return true;
    }

    [SuppressMessage("Security", "CA5350", Justification = "SHA-1 is only the certificate thumbprint that Microsoft publishes; nothing is authenticated with it.")]
    private static RevokedCertificate DescribeCertificate(byte[] der)
    {
        var thumbprint = Convert.ToHexStringLower(SHA1.HashData(der));
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return new RevokedCertificate(certificate.Subject, thumbprint, null, null);
        }
        catch (CryptographicException)
        {
            return new RevokedCertificate(string.Create(CultureInfo.InvariantCulture, $"(unreadable certificate {thumbprint})"), thumbprint, null, null);
        }
    }

    internal static BootrixException Invalid(string reason, Exception? inner = null) =>
        new(ErrorCode.RevocationDataInvalid, reason, inner) { Arguments = [reason] };
}
