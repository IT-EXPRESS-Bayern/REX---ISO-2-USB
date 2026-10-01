// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Planning;

/// <summary>Resource keys of the warnings the planner can raise; each has a German and an English text with the arguments noted here.</summary>
public static class PlanWarningCodes
{
    private const string Prefix = "Plan.Warning.";

    public const string FixedDisk = Prefix + "FixedDisk";

    public const string FourKnUsbBridge = Prefix + "FourKnUsbBridge";

    public const string FourKnBios = Prefix + "FourKnBios";

    public const string FourKnLegacyIgnored = Prefix + "FourKnLegacyIgnored";

    public const string FourKnHybridImage = Prefix + "FourKnHybridImage";

    /// <summary>Argument 0: the device size.</summary>
    public const string LargeDriveGpt = Prefix + "LargeDriveGpt";

    /// <summary>Arguments: usable size, device size.</summary>
    public const string MbrCapped = Prefix + "MbrCapped";

    public const string BiosOnGpt = Prefix + "BiosOnGpt";

    public const string LegacyNeedsMbr = Prefix + "LegacyNeedsMbr";

    public const string LegacyNeedsBios = Prefix + "LegacyNeedsBios";

    /// <summary>Arguments: partition size, device size.</summary>
    public const string LegacyCapacityLimited = Prefix + "LegacyCapacityLimited";

    public const string GrubNeedsGap = Prefix + "GrubNeedsGap";

    public const string NoEfiBootFiles = Prefix + "NoEfiBootFiles";

    public const string NoBiosBootFiles = Prefix + "NoBiosBootFiles";

    public const string ArmHasNoBios = Prefix + "ArmHasNoBios";

    public const string RawCopyReadOnlyMedia = Prefix + "RawCopyReadOnlyMedia";

    public const string PersistenceUnsupported = Prefix + "PersistenceUnsupported";

    /// <summary>Arguments: requested size, granted size.</summary>
    public const string PersistenceReduced = Prefix + "PersistenceReduced";

    public const string LinuxNtfsSupport = Prefix + "LinuxNtfsSupport";

    public const string ExFatCasper = Prefix + "ExFatCasper";

    /// <summary>Arguments: size of the volume, size left unused.</summary>
    public const string CapacityNotUsed = Prefix + "CapacityNotUsed";

    public const string DosNeedsMbr = Prefix + "DosNeedsMbr";

    public const string SuperfloppyBoot = Prefix + "SuperfloppyBoot";

    /// <summary>Argument 0: the device size.</summary>
    public const string NonStandardFloppySize = Prefix + "NonStandardFloppySize";

    public const string UefiNtfsCa2011 = Prefix + "UefiNtfsCa2011";
}
