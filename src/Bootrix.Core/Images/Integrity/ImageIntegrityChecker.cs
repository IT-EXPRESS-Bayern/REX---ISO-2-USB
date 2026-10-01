// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Images.Iso;
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Images.Integrity;

public sealed record IntegrityReport(IReadOnlyList<ImageWarning> Findings)
{
    /// <summary>Nothing indicates that the image is cut off or damaged.</summary>
    public bool IsComplete => Findings.All(finding => finding.Severity != WarningSeverity.Error);
}

/// <summary>
/// Detects truncated downloads and damaged images from the structure alone, so a short file is reported before
/// anything is written to a disk: the sizes announced by ISO 9660, UDF, GPT, MBR and WIM headers are compared with
/// the file length, and compressed containers are checked for their end markers. Formats without a trailer
/// (gzip, .Z) can only be verified by decoding them completely, see <see cref="VerifyCompressedAsync"/>.
/// </summary>
public static class ImageIntegrityChecker
{
    /// <summary>Enough to reach the UDF anchor at sector 256 and the descriptors it points to.</summary>
    private const int CompressedPrefixBytes = 2 * 1024 * 1024;

    public static IntegrityReport Check(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Check(stream, path, cancellationToken);
    }

    public static IntegrityReport Check(Stream stream, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var findings = new List<ImageWarning>();
        if (fileName is not null)
        {
            findings.AddRange(CheckName(fileName));
        }

        var start = stream.Position;
        var head = new byte[CompressionSniffer.HeaderLength];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = start;
        var format = CompressionSniffer.Detect(head.AsSpan(0, read));

        if (format == CompressionFormat.None)
        {
            findings.AddRange(CheckImage(stream, stream.Length, fileName));
            return new IntegrityReport(findings);
        }

        stream.Position = start;
        try
        {
            using var decoded = CompressedImageStream.Open(stream, new CompressedImageOptions { CheckStructure = false }, leaveOpen: true);
            findings.AddRange(CompressedFindings(format, decoded.Structure));
            if (decoded.Structure.Verdict == StructureVerdict.Broken)
            {
                return new IntegrityReport(findings);
            }

            using var prefix = PrefixBuffer.Read(decoded, CompressedPrefixBytes);
            findings.AddRange(CheckImage(prefix, decoded.UncompressedLength, null));
        }
        catch (BootrixException ex) when (ex.Code is ErrorCode.ImageUnreadable or ErrorCode.ImageUnsupported or ErrorCode.ImageEncrypted)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.CompressedIncomplete, WarningSeverity.Error, format.ToString(), ex.Detail));
        }

        return new IntegrityReport(findings);
    }

    public static IReadOnlyList<ImageWarning> CheckName(string path) =>
        PartialDownloadExtension(path) is { } extension
            ? [new ImageWarning(ImageWarningKeys.PartialDownload, WarningSeverity.Error, extension)]
            : [];

    /// <summary>
    /// Image on the disk that is about to be overwritten: writing would destroy the source halfway through.
    /// </summary>
    public static bool IsImageOnTarget(string imagePath, IEnumerable<string> targetVolumeRoots, IEnumerable<string>? otherVolumeRoots = null) =>
        VolumePaths.IsOwnedBy(imagePath, targetVolumeRoots, otherVolumeRoots ?? []);

    public static bool IsSameVolume(string left, string right) => VolumePaths.Normalize(left) == VolumePaths.Normalize(right);

    public static bool IsPartialDownloadName(string path) => PartialDownloadExtension(path) is not null;

    private static readonly string[] PartialExtensions =
        [".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".unconfirmed", ".!ut", ".bc!", ".aria2"];

    private static string? PartialDownloadExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return PartialExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? extension.ToLowerInvariant() : null;
    }

    /// <summary>Findings for the verdict of the header and trailer check of a compressed file.</summary>
    internal static List<ImageWarning> CompressedFindings(CompressionFormat format, StructureReport report)
    {
        var findings = new List<ImageWarning>();
        switch (report.Verdict)
        {
            case StructureVerdict.Broken:
                findings.Add(new ImageWarning(ImageWarningKeys.CompressedIncomplete, WarningSeverity.Error, format.ToString(), report.Problem));
                break;
            case StructureVerdict.Unverifiable when format is CompressionFormat.GZip or CompressionFormat.Compress or CompressionFormat.Lzma:
                findings.Add(new ImageWarning(ImageWarningKeys.CompressedUnverified, WarningSeverity.Info, format.ToString()));
                break;
        }

        return findings;
    }

    /// <summary>
    /// Structure checks of an image that is available as a seekable stream. <paramref name="imageLength"/> is the length the
    /// complete image is supposed to have: the file length, or the decoded size of a compressed image when that is known
    /// (null skips the checks that compare against it, as <paramref name="image"/> may then hold only the start of the image).
    /// </summary>
    internal static List<ImageWarning> CheckImage(Stream image, long? imageLength, string? fileName = null)
    {
        var findings = new List<ImageWarning>();
        var container = ImageContainerSniffer.Detect(image);

        // A fixed VHD is a raw disk with the footer at the very end, so a cut-off one looks like any other raw disk;
        // the file name is the only hint that the footer should be there.
        if (container != ImageContainer.Vhd && fileName is not null && Path.GetExtension(fileName).Equals(".vhd", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new ImageWarning(ImageWarningKeys.VhdFooterMissing, WarningSeverity.Error));
        }

        var complete = imageLength is { } length && length == image.Length;

        switch (container)
        {
            case ImageContainer.Iso9660 or ImageContainer.IsoUdfBridge or ImageContainer.Udf:
                CheckOptical(image, imageLength, container, findings);
                CheckGptBackup(DiskLayoutReader.Read(image), imageLength, WarningSeverity.Warning, findings);
                break;
            case ImageContainer.RawDisk:
                CheckDisk(DiskLayoutReader.Read(image), imageLength, findings);
                break;
            case ImageContainer.Wim:
                CheckWim(image, imageLength, findings);
                break;
            case ImageContainer.Vhd when complete:
                CheckVhd(image, findings);
                break;
        }

        return findings;
    }

    private static void CheckOptical(Stream image, long? imageLength, ImageContainer container, List<ImageWarning> findings)
    {
        if (imageLength is not { } length)
        {
            return;
        }

        var volume = container != ImageContainer.Udf ? Iso9660Reader.Read(image) : null;
        if (volume is not null && volume.VolumeBytes > length)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.IsoTruncated, WarningSeverity.Error, volume.VolumeBytes, length));
        }

        var udf = container != ImageContainer.Iso9660 ? UdfAnchor.Read(image) : null;
        if (udf is not null && udf.ExpectedBytes > length)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.UdfTruncated, WarningSeverity.Error, udf.ExpectedBytes, length));
        }
    }

    private static void CheckDisk(DiskLayout layout, long? imageLength, List<ImageWarning> findings)
    {
        if (imageLength is { } length)
        {
            foreach (var partition in layout.MbrPartitions.Where(p => !p.IsProtective))
            {
                var needed = partition.EndSector * 512;
                if (needed > length)
                {
                    findings.Add(new ImageWarning(ImageWarningKeys.PartitionBeyondEnd, WarningSeverity.Error, partition.Slot + 1, needed, length));
                }
            }
        }

        CheckGptBackup(layout, imageLength, WarningSeverity.Error, findings);
    }

    /// <summary>
    /// The backup GPT is the last sector of the disk the image was made from. A raw copy that lacks it has been cut off;
    /// on a hybrid ISO the backup sits at the end of the ISO area, so a missing one is less certain evidence.
    /// </summary>
    private static void CheckGptBackup(DiskLayout layout, long? imageLength, WarningSeverity severity, List<ImageWarning> findings)
    {
        if (!layout.HasGpt)
        {
            return;
        }

        if (!layout.GptHeaderValid)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.GptHeaderInvalid, WarningSeverity.Warning));
        }

        if (imageLength is { } length)
        {
            var needed = (layout.GptBackupSector + 1) * layout.GptSectorSize;
            if (needed > length)
            {
                findings.Add(new ImageWarning(ImageWarningKeys.GptBackupMissing, severity, needed, length));
            }
        }
    }

    private static void CheckWim(Stream image, long? imageLength, List<ImageWarning> findings)
    {
        var header = WimHeader.Parse(ImageContainerSniffer.ReadAt(image, 0, WimHeader.Size));
        if (imageLength is { } length && header.ExpectedLength > length)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.WimTruncated, WarningSeverity.Error, header.ExpectedLength, length));
        }
    }

    /// <summary>
    /// A fixed VHD ends with the footer; a dynamic one has a copy at the start and the original at the end.
    /// </summary>
    private static void CheckVhd(Stream image, List<ImageWarning> findings)
    {
        var tail = ImageContainerSniffer.ReadAt(image, image.Length - 512, 512);
        if (!ImageContainerSniffer.IsVhdFooter(tail))
        {
            findings.Add(new ImageWarning(ImageWarningKeys.VhdFooterMissing, WarningSeverity.Error));
        }
    }

    /// <summary>
    /// Decodes a compressed image completely and returns its size. This is the only way to prove that gzip and .Z
    /// files are intact, and it also checks the CRCs and checksums the formats carry. Reading costs one pass over the
    /// image; the result is a verification, nothing is written.
    /// </summary>
    public static async Task<long> VerifyCompressedAsync(string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var decoded = await CompressedImageStream.OpenAsync(path, null, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await decoded.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            progress?.Report(decoded.CompressedBytesConsumed);
        }

        return total;
    }
}
