// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Policy;

/// <summary>Resource keys of the reasons and warnings the image policy produces.</summary>
public static class ImagePolicyKeys
{
    // Reasons ("Why?").
    public const string VendorMandatesRawCopy = "Image.Policy.Reason.VendorMandatesRawCopy";
    public const string IsoModeOnly = "Image.Policy.Reason.IsoModeOnly";
    public const string NotBootableAsDisk = "Image.Policy.Reason.NotBootableAsDisk";
    public const string RawImageOnly = "Image.Policy.Reason.RawImageOnly";
    public const string HybridRawCopy = "Image.Policy.Reason.HybridRawCopy";
    public const string PersistenceNeedsExtract = "Image.Policy.Reason.PersistenceNeedsExtract";
    public const string PersistenceViaPartition = "Image.Policy.Reason.PersistenceViaPartition";
    public const string WindowsSetupExtract = "Image.Policy.Reason.WindowsSetupExtract";
    public const string WindowsStickRawCopy = "Image.Policy.Reason.WindowsStickRawCopy";
    public const string WimApplied = "Image.Policy.Reason.WimApplied";
    public const string DataImageAsk = "Image.Policy.Reason.DataImageAsk";
    public const string FloppyImageRawCopy = "Image.Policy.Reason.FloppyImageRawCopy";
    public const string PreferredModeUsed = "Image.Policy.Reason.PreferredModeUsed";
    public const string PreferredModeRefused = "Image.Policy.Reason.PreferredModeRefused";

    // Warnings.

    /// <summary>DD writes a read-only ISO 9660 layout: Windows offers to format the stick, the remaining space is unallocated.</summary>
    public const string DdReadOnlyLayout = "Image.Policy.Warning.DdReadOnlyLayout";

    public const string GptBackupAtIsoEnd = "Image.Policy.Warning.GptBackupAtIsoEnd";
    public const string UefiOnlyEdk2 = "Image.Policy.Warning.UefiOnlyEdk2";
    public const string GptProtectiveMbrLegacy = "Image.Policy.Warning.GptProtectiveMbrLegacy";
    public const string Sector4kTarget = "Image.Policy.Warning.Sector4kTarget";
    public const string SuperfloppyUnreliable = "Image.Policy.Warning.SuperfloppyUnreliable";

    /// <summary>{0} the label of the image, {1} the label that fits on the target.</summary>
    public const string LabelChanged = "Image.Policy.Warning.LabelChanged";

    public const string FatFileLimit = "Image.Policy.Warning.FatFileLimit";
    public const string NtfsExfatHelper = "Image.Policy.Warning.NtfsExfatHelper";
    public const string CasperExfat = "Image.Policy.Warning.CasperExfat";
    public const string BootloaderOffline = "Image.Policy.Warning.BootloaderOffline";
    public const string LegacyBiosOnly = "Image.Policy.Warning.LegacyBiosOnly";
    public const string BsdIsoForOptical = "Image.Policy.Warning.BsdIsoForOptical";
    public const string NotBootableOnPc = "Image.Policy.Warning.NotBootableOnPc";
    public const string RevokedLoaders = "Image.Policy.Warning.RevokedLoaders";
    public const string PersistenceUnsupported = "Image.Policy.Warning.PersistenceUnsupported";
    public const string PersistenceBootArgument = "Image.Policy.Warning.PersistenceBootArgument";

    /// <summary>{0} bytes the image needs, {1} bytes the target offers.</summary>
    public const string ImageLargerThanTarget = "Image.Policy.Warning.ImageLargerThanTarget";

    public const string EsxiSyslinux = "Image.Policy.Warning.EsxiSyslinux";
}
