// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Optical;

/// <summary>Control codes of the CD-ROM class driver (ntddcdrm.h, ntddcdvd.h), built like the CTL_CODE macro does.</summary>
internal static class OpticalIoctl
{
    private const uint DeviceTypeCdRom = 0x02;
    private const uint DeviceTypeDvd = 0x33;
    private const uint MethodBuffered = 0;
    private const uint ReadAccess = 1;

    public static readonly uint ReadTocEx = Ioctl.CtlCode(DeviceTypeCdRom, 0x15, MethodBuffered, ReadAccess);

    public static readonly uint GetDriveGeometryEx = Ioctl.CtlCode(DeviceTypeCdRom, 0x14, MethodBuffered, ReadAccess);

    public static readonly uint SetSpeed = Ioctl.CtlCode(DeviceTypeCdRom, 0x18, MethodBuffered, ReadAccess);

    public static readonly uint DvdReadStructure = Ioctl.CtlCode(DeviceTypeDvd, 0x450, MethodBuffered, ReadAccess);

    /// <summary>Format numbers of READ TOC EX.</summary>
    public const byte TocFormatToc = 0;

    public const byte TocFormatFullToc = 2;

    /// <summary>DvdCopyrightDescriptor in DVD_STRUCTURE_FORMAT.</summary>
    public const uint DvdCopyrightDescriptor = 1;
}
