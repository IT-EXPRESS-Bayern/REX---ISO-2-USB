// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Devices.Cdrom;
using Windows.Win32.Devices.Dvd;
using Windows.Win32.Foundation;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Reads 2048-byte sectors from a disc through its volume (\\.\E:) or, if it has no drive letter, through the
/// CD-ROM device. The handle is opened for unbuffered I/O, so every request goes to the drive and the data is
/// not served from a cache that may hide a defect; that requires an aligned buffer, which is kept inside so
/// callers can pass any span.
/// </summary>
public sealed unsafe class WindowsSectorReader : ISectorReader, IDiscInspector
{
    private const int SectorSize = SectorMath.SectorSize;

    /// <summary>One request never covers more than 512 KiB; larger transfers only make a failure cost more sectors.</summary>
    private const int MaxRequestSectors = 256;

    private const int ErrorBadUnit = 20;
    private const int ErrorNotReady = 21;
    private const int ErrorDeviceNotExist = 55;
    private const int ErrorMediaChanged = 1110;
    private const int ErrorNoMediaInDrive = 1112;
    private const int ErrorDeviceNotConnected = 1167;

    /// <summary>
    /// Win32 error codes the NTSTATUS values for scrambled sectors map to. Several of the CSS statuses share the
    /// generic codes, which are useless as a signal, so only distinctive ones are kept; the list may well be empty,
    /// the copyright structure is the main way protected DVDs are recognised.
    /// </summary>
    private static readonly Lazy<HashSet<int>> ProtectedContentErrors = new(BuildProtectedContentErrors);

    private readonly SafeFileHandle _handle;
    private readonly AlignedBuffer _buffer = new(MaxRequestSectors * SectorSize);
    private bool _disposed;

    private WindowsSectorReader(SafeFileHandle handle, string name, long sectorCount)
    {
        _handle = handle;
        Name = name;
        SectorCount = sectorCount;
    }

    public string Name { get; }

    public long SectorCount { get; }

    public static WindowsSectorReader Open(OpticalDrive drive)
    {
        var path = DevicePath(drive);
        var handle = OpenHandle(path);
        try
        {
            // The volume of a disc refuses reads beyond its file system unless told otherwise, which would cut off rewritable discs.
            DeviceIo.TryControl(handle, Ioctl.FsctlAllowExtendedDasdIo);
            return new WindowsSectorReader(handle, drive.DisplayName, QueryCapacity(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The disc in a drive IMAPI cannot describe (a reader): present or not, and how big. The type stays
    /// unknown, so the result is never offered for writing.
    /// </summary>
    public static OpticalMedia ProbeMedia(OpticalDrive drive)
    {
        using var handle = DeviceIo.TryOpen(DevicePath(drive), 0, Kernel32.FileShareRead | Kernel32.FileShareWrite);
        if (handle is null || !DeviceIo.TryControl(handle, Ioctl.StorageCheckVerify2))
        {
            return OpticalMedia.None;
        }

        // No state bit says "a disc is here" on its own; Finalized is what a disc that cannot take data looks like.
        return new OpticalMedia { State = OpticalMediaState.Finalized, IsSupported = false, TotalSectors = QueryCapacity(handle) };
    }

    public SectorReadResult Read(long lba, int count, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        count = Math.Min(count, MaxRequestSectors);
        if (buffer.Length < count * SectorSize)
        {
            throw new ArgumentException("The buffer is smaller than the requested sectors.", nameof(buffer));
        }

        using var errorMode = new ErrorModeScope();
        var bytes = (uint)(count * SectorSize);

        // Optical drives do not advance the position by themselves between requests, so it is set every time.
        uint read = 0;
        fixed (byte* target = _buffer.GetSpan())
        {
            if (OpticalNative.SetFilePointerEx(_handle, lba * SectorSize, out _, 0)
                && OpticalNative.ReadFile(_handle, target, bytes, out read, 0))
            {
                var sectors = (int)(read / SectorSize);
                _buffer.GetSpan()[..(sectors * SectorSize)].CopyTo(buffer);
                return sectors > 0 ? SectorReadResult.Success(sectors) : SectorReadResult.Failure(0);
            }
        }

        return Classify(Marshal.GetLastPInvokeError());
    }

    public bool TrySetReadSpeed(int kilobytesPerSecond)
    {
        if (_disposed)
        {
            return false;
        }

        var request = new CDROM_SET_SPEED
        {
            RequestType = CDROM_SPEED_REQUEST.CdromSetSpeed,
            ReadSpeed = (ushort)Math.Clamp(kilobytesPerSecond, 1, ushort.MaxValue),
            WriteSpeed = ushort.MaxValue,
            RotationControl = WRITE_ROTATION.CdromDefaultRotation,
        };
        return DeviceIo.TryControl(_handle, OpticalIoctl.SetSpeed, new ReadOnlySpan<byte>(&request, sizeof(CDROM_SET_SPEED)), [], out _, out _);
    }

    public DiscToc? ReadToc()
    {
        if (QueryToc(OpticalIoctl.TocFormatFullToc, session: 1, msf: true) is { } full && TocParser.ParseFull(full) is { } toc)
        {
            return toc;
        }

        return QueryToc(OpticalIoctl.TocFormatToc, session: 0, msf: false) is { } basic ? TocParser.ParseBasic(basic, addressesAreMsf: false) : null;
    }

    public DiscCopyrightInfo? ReadCopyrightInfo()
    {
        var request = new DVD_READ_STRUCTURE { Format = DVD_STRUCTURE_FORMAT.DvdCopyrightDescriptor };
        var output = new byte[16];
        if (!DeviceIo.TryControl(_handle, OpticalIoctl.DvdReadStructure, new ReadOnlySpan<byte>(&request, sizeof(DVD_READ_STRUCTURE)), output, out var returned, out _)
            || returned < 8)
        {
            return null;
        }

        // A four-byte descriptor header (length, reserved) comes first, then protection type and region information.
        return DiscCopyrightInfo.FromDescriptor(output[4], output[5]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
        ((IDisposable)_buffer).Dispose();
    }

    private static string DevicePath(OpticalDrive drive) => drive switch
    {
        { DriveLetter: { } letter } => $@"\\.\{letter}",
        { DeviceNumber: { } number } => $@"\\.\CdRom{number}",
        _ => throw new BootrixException(ErrorCode.DeviceNotFound, drive.DisplayName),
    };

    private static SafeFileHandle OpenHandle(string path)
    {
        try
        {
            return DeviceIo.Open(
                path,
                Kernel32.GenericRead,
                Kernel32.FileShareRead | Kernel32.FileShareWrite,
                Kernel32.FileFlagNoBuffering | Kernel32.FileFlagSequentialScan);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Kernel32.ErrorAccessDenied)
        {
            throw new BootrixException(ErrorCode.DeviceBusy, path, ex) { Arguments = ["access denied; another program may hold the drive"] };
        }
        catch (Win32Exception ex)
        {
            throw new BootrixException(ErrorCode.DeviceNotFound, path, ex);
        }
    }

    private static long QueryCapacity(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX is the 24-byte DISK_GEOMETRY followed by the disk size.
        var geometry = new byte[256];
        if (DeviceIo.TryControl(handle, OpticalIoctl.GetDriveGeometryEx, [], geometry, out var returned, out _) && returned >= 32)
        {
            var size = BitConverter.ToInt64(geometry, 24);

            // Rewritable media often report a size of a sector or two; that is not a capacity.
            if (size > 2 * SectorSize)
            {
                return size / SectorSize;
            }
        }

        var length = new byte[8];
        if (DeviceIo.TryControl(handle, Ioctl.DiskGetLengthInfo, [], length, out returned, out _) && returned >= 8)
        {
            var size = BitConverter.ToInt64(length);
            return size > 2 * SectorSize ? size / SectorSize : 0;
        }

        return 0;
    }

    private byte[]? QueryToc(byte format, byte session, bool msf)
    {
        // CDROM_READ_TOC_EX: format in the low four bits, the MSF flag in the top bit, then the first session or track.
        ReadOnlySpan<byte> input = [(byte)(format | (msf ? 0x80 : 0)), session, 0, 0];
        var output = new byte[4096];
        return DeviceIo.TryControl(_handle, OpticalIoctl.ReadTocEx, input, output, out var returned, out _) ? output[..returned] : null;
    }

    private SectorReadResult Classify(int error)
    {
        if (ProtectedContentErrors.Value.Contains(error))
        {
            return SectorReadResult.Failure(0, SectorReadStatus.ProtectedContent);
        }

        var gone = error is ErrorNoMediaInDrive or ErrorMediaChanged or ErrorDeviceNotConnected or ErrorBadUnit or ErrorDeviceNotExist
            || (error == ErrorNotReady && !DeviceIo.TryControl(_handle, Ioctl.StorageCheckVerify2));
        if (gone)
        {
            throw new BootrixException(ErrorCode.DeviceNotFound, $"{Name}: disc removed or drive lost (error {error})", new Win32Exception(error));
        }

        // Anything else is a read error of the disc: CRC, sector not found, I/O device error, general failure.
        return SectorReadResult.Failure(0);
    }

    private static HashSet<int> BuildProtectedContentErrors()
    {
        const int genericMapping = 317;
        var generic = new HashSet<int> { 0, genericMapping, 5, 23, 27, 30, 31, 1117 };
        NTSTATUS[] statuses =
        [
            NTSTATUS.STATUS_COPY_PROTECTION_FAILURE,
            NTSTATUS.STATUS_CSS_AUTHENTICATION_FAILURE,
            NTSTATUS.STATUS_CSS_KEY_NOT_PRESENT,
            NTSTATUS.STATUS_CSS_KEY_NOT_ESTABLISHED,
            NTSTATUS.STATUS_CSS_SCRAMBLED_SECTOR,
            NTSTATUS.STATUS_CSS_REGION_MISMATCH,
            NTSTATUS.STATUS_CSS_RESETS_EXHAUSTED,
        ];

        try
        {
            return [.. statuses.Select(s => (int)PInvoke.RtlNtStatusToDosError(s)).Where(code => !generic.Contains(code))];
        }
        catch (DllNotFoundException)
        {
            return [];
        }
    }
}
