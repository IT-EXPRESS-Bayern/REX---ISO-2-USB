// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images;

/// <summary>Resource keys of the findings produced by the inspector and the integrity checker.</summary>
public static class ImageWarningKeys
{
    // Integrity: the image is incomplete or damaged. Arguments in braces.

    /// <summary>{0} bytes expected by the volume descriptor, {1} bytes present.</summary>
    public const string IsoTruncated = "Image.Integrity.IsoTruncated";

    /// <summary>{0} bytes expected by the UDF partition, {1} bytes present.</summary>
    public const string UdfTruncated = "Image.Integrity.UdfTruncated";

    /// <summary>{0} partition number, {1} bytes needed to hold it, {2} bytes present.</summary>
    public const string PartitionBeyondEnd = "Image.Integrity.PartitionBeyondEnd";

    /// <summary>{0} bytes needed to hold the backup GPT, {1} bytes present.</summary>
    public const string GptBackupMissing = "Image.Integrity.GptBackupMissing";

    public const string GptHeaderInvalid = "Image.Integrity.GptHeaderInvalid";

    /// <summary>{0} bytes expected from the WIM header, {1} bytes present.</summary>
    public const string WimTruncated = "Image.Integrity.WimTruncated";

    public const string VhdFooterMissing = "Image.Integrity.VhdFooterMissing";

    /// <summary>{0} format name, {1} technical detail.</summary>
    public const string CompressedIncomplete = "Image.Integrity.CompressedIncomplete";

    /// <summary>{0} format name; the format has no trailer that could prove completeness.</summary>
    public const string CompressedUnverified = "Image.Integrity.CompressedUnverified";

    /// <summary>{0} the extension of an unfinished download.</summary>
    public const string PartialDownload = "Image.Integrity.PartialDownload";

    public const string ImageOnTarget = "Image.Integrity.ImageOnTarget";

    // Inspection findings.

    public const string CompressedSizeUnknown = "Image.Warning.CompressedSizeUnknown";

    /// <summary>{0} number of MiB that were decoded.</summary>
    public const string AnalysisPartial = "Image.Warning.AnalysisPartial";

    public const string FileTreeIncomplete = "Image.Warning.FileTreeIncomplete";

    public const string FileSystemUnreadable = "Image.Warning.FileSystemUnreadable";

    /// <summary>{0} path of the WIM inside the image.</summary>
    public const string WimUnreadable = "Image.Warning.WimUnreadable";

    public const string Win7EfiLoaderMissing = "Image.Warning.Win7EfiLoaderMissing";

    public const string BootImageUnreadable = "Image.Warning.BootImageUnreadable";

    public const string NotBootable = "Image.Warning.NotBootable";

    public const string Sector4kImage = "Image.Warning.Sector4kImage";
}
