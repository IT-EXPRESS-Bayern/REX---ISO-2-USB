// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Linux;

/// <summary>Resource keys of the notes the Linux media builder leaves in the job log; each has a German and an English text.</summary>
public static class LinuxNoticeKeys
{
    private const string Prefix = "Linux.Notice.";

    /// <summary>Arguments: Syslinux release that is installed, version found in the image.</summary>
    public const string SyslinuxInstalled = Prefix + "SyslinuxInstalled";

    /// <summary>Arguments: version found in the image, release that is installed.</summary>
    public const string SyslinuxVersionDiffers = Prefix + "SyslinuxVersionDiffers";

    /// <summary>Arguments: number of replaced or added modules.</summary>
    public const string SyslinuxModulesReplaced = Prefix + "SyslinuxModulesReplaced";

    public const string GrubInstalled = Prefix + "GrubInstalled";

    /// <summary>Argument: the file system of the medium.</summary>
    public const string GrubInsteadOfSyslinuxFileSystem = Prefix + "GrubInsteadOfSyslinuxFileSystem";

    /// <summary>Arguments: version found in the image.</summary>
    public const string GrubInsteadOfSyslinuxVersion = Prefix + "GrubInsteadOfSyslinuxVersion";

    public const string GrubMenuRelocated = Prefix + "GrubMenuRelocated";

    public const string BiosNeeds512ByteSectors = Prefix + "BiosNeeds512ByteSectors";

    /// <summary>Argument: the file system of the medium.</summary>
    public const string NoBiosLoader = Prefix + "NoBiosLoader";

    /// <summary>Arguments: label of the image, label of the medium, number of lines.</summary>
    public const string LabelRewritten = Prefix + "LabelRewritten";

    /// <summary>Argument: the parameter, number of lines.</summary>
    public const string PersistenceParameterAdded = Prefix + "PersistenceParameterAdded";

    public const string PersistenceNotPatched = Prefix + "PersistenceNotPatched";

    public const string PersistenceNoBootLine = Prefix + "PersistenceNoBootLine";

    public const string EsxiPartitionAdded = Prefix + "EsxiPartitionAdded";

    /// <summary>Arguments: path, reason.</summary>
    public const string FileSkipped = Prefix + "FileSkipped";

    public const string EfiLoadersFromBootImage = Prefix + "EfiLoadersFromBootImage";

    /// <summary>Argument: number of lines.</summary>
    public const string ChecksumsUpdated = Prefix + "ChecksumsUpdated";
}
