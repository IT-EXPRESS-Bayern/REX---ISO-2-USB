// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace Bootrix.Windows.Optical;

/// <summary>An IStream for IMAPI together with whatever has to be released after it.</summary>
internal sealed class ImapiStream : IDisposable
{
    private readonly Stream? _managed;
    private readonly ComScope _com = new();

    private ImapiStream(IStream stream, Stream? managed, long sectors)
    {
        Stream = stream;
        _managed = managed;
        Sectors = sectors;
    }

    public IStream Stream { get; }

    /// <summary>Length in 2048-byte sectors, which is what IMAPI checks against the free space of the disc.</summary>
    public long Sectors { get; }

    /// <summary>Wraps a .NET stream. IMAPI refuses streams that are not whole sectors long, so the caller pads first.</summary>
    public static ImapiStream FromManaged(Stream stream)
    {
        if (stream.Length % SectorMath.SectorSize != 0)
        {
            throw new ArgumentException("The stream must be a whole number of sectors long.", nameof(stream));
        }

        return new ImapiStream(new ManagedStream(stream), stream, stream.Length / SectorMath.SectorSize);
    }

    /// <summary>
    /// Opens a disc image. A plain file that is a whole number of sectors long goes to IMAPI as a shell file stream, which
    /// IMAPI reads without any managed code in between; everything else is padded and wrapped.
    /// </summary>
    public static ImapiStream FromImage(DiscImageSource image)
    {
        if (image.FilePath is { } path && image.LengthBytes % SectorMath.SectorSize == 0)
        {
            return FromFile(path);
        }

        return FromManaged(image.OpenPadded());
    }

    /// <summary>A shell stream on a file, shared for reading only so another program can keep it open.</summary>
    public static ImapiStream FromFile(string path)
    {
        // STGM_READ | STGM_SHARE_DENY_WRITE
        const uint mode = 0x20;
        PInvoke.SHCreateStreamOnFileEx(path, mode, 0, false, null, out var stream).ThrowOnFailure();
        var result = new ImapiStream(stream, null, new FileInfo(path).Length / SectorMath.SectorSize);
        result._com.Add(stream);
        return result;
    }

    public void Dispose()
    {
        _com.Dispose();
        _managed?.Dispose();
    }
}
