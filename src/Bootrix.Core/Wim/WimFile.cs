// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Wim;

public enum WimCompression
{
    None = 0,
    Xpress = 1,
    Lzx = 2,
    Lzms = 3,
}

public sealed record WimInfo(
    int ImageCount,
    int BootIndex,
    int Version,
    int ChunkSize,
    int PartNumber,
    int TotalParts,
    WimCompression Compression,
    long TotalBytes);

/// <summary>
/// A WIM or ESD file opened through wimlib. Used for the operations that need real WIM handling:
/// splitting an install.wim that does not fit on FAT32, exporting a single edition, converting
/// ESD to WIM. Reading metadata without wimlib is done elsewhere; this is for changing files.
/// </summary>
public sealed unsafe class WimFile : IDisposable
{
    private static readonly Lock InitGate = new();
    private static bool _initialised;

    private nint _handle;
    private GCHandle _context;
    private Action<int, nint>? _progress;
    private CancellationToken _cancellation;

    private WimFile(nint handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    public static bool IsAvailable => WimLibNative.IsAvailable();

    public static WimFile Open(string path, bool checkIntegrity = false)
    {
        EnsureInitialised();
        using var native = new NativeString(path);
        var code = WimLibNative.OpenWim(native.Pointer, checkIntegrity ? WimLibNative.OpenCheckIntegrity : 0, out var handle);
        ThrowIfFailed(code, $"open {path}");
        return new WimFile(handle, path);
    }

    public WimInfo Info
    {
        get
        {
            ThrowIfDisposed();
            Span<byte> info = stackalloc byte[WimLibNative.InfoSize];
            fixed (byte* pointer = info)
            {
                ThrowIfFailed(WimLibNative.GetWimInfo(_handle, pointer), "get info");
            }

            return ParseInfo(info);
        }
    }

    /// <summary>A property from the image metadata, e.g. "NAME", "WINDOWS/VERSION/BUILD" or "WINDOWS/ARCH"; null when absent.</summary>
    public string? GetImageProperty(int image, string property)
    {
        ThrowIfDisposed();
        using var name = new NativeString(property);
        return NativeString.Read(WimLibNative.GetImageProperty(_handle, image, name.Pointer));
    }

    /// <summary>
    /// Writes <paramref name="firstPart"/> and following parts (name2.swm, name3.swm, ...), none larger
    /// than <paramref name="maxPartBytes"/>. Compressed resources are copied as they are, nothing is recompressed.
    /// </summary>
    public void Split(string firstPart, long maxPartBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _cancellation = cancellationToken;
        RegisterProgress((message, info) =>
        {
            if (message == WimLibNative.MsgSplitEndPart || message == WimLibNative.MsgSplitBeginPart)
            {
                // struct split: total_bytes at 0, completed_bytes at 8
                var total = *(ulong*)info;
                var done = *(ulong*)(info + 8);
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        });

        try
        {
            using var name = new NativeString(firstPart);
            var code = WimLibNative.Split(_handle, name.Pointer, (ulong)maxPartBytes, 0);
            ThrowIfCanceled(cancellationToken);
            ThrowIfFailed(code, $"split into {firstPart}");
        }
        finally
        {
            UnregisterProgress();
        }
    }

    /// <summary>Writes one image (or all) to a new file; used to extract a single edition from a multi-edition install.wim.</summary>
    public void WriteImage(string destination, int image, WimCompression? compression = null, bool recompress = false, bool solid = false, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _cancellation = cancellationToken;
        RegisterProgress((message, info) =>
        {
            if (message == WimLibNative.MsgWriteStreams)
            {
                // struct write_streams: total_bytes at 0, completed_bytes at 16
                var total = *(ulong*)info;
                var done = *(ulong*)(info + 16);
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        });

        try
        {
            if (compression is { } type)
            {
                ThrowIfFailed(WimLibNative.SetOutputCompressionType(_handle, (int)type), "set compression");
            }

            using var path = new NativeString(destination);
            var flags = (recompress ? WimLibNative.WriteRecompress : 0) | (solid ? WimLibNative.WriteSolid : 0);
            var code = WimLibNative.Write(_handle, path.Pointer, image, flags, 0);
            ThrowIfCanceled(cancellationToken);
            ThrowIfFailed(code, $"write {destination}");
        }
        finally
        {
            UnregisterProgress();
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            WimLibNative.Free(_handle);
            _handle = 0;
        }

        if (_context.IsAllocated)
        {
            _context.Free();
        }
    }

    internal static WimInfo ParseInfo(ReadOnlySpan<byte> info)
    {
        // struct wimlib_wim_info: guid[16], image_count, boot_index, wim_version, chunk_size, part_number(u16),
        // total_parts(u16), compression_type, total_bytes(u64)
        return new WimInfo(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(info[20..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]),
            BinaryPrimitives.ReadUInt16LittleEndian(info[32..]),
            BinaryPrimitives.ReadUInt16LittleEndian(info[34..]),
            (WimCompression)BinaryPrimitives.ReadInt32LittleEndian(info[36..]),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(info[40..]));
    }

    private void RegisterProgress(Action<int, nint> handler)
    {
        _progress = handler;
        _context = GCHandle.Alloc(this);
        WimLibNative.RegisterProgressFunction(_handle, &OnProgress, GCHandle.ToIntPtr(_context));
    }

    private void UnregisterProgress()
    {
        WimLibNative.RegisterProgressFunction(_handle, null, 0);
        _progress = null;
        if (_context.IsAllocated)
        {
            _context.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnProgress(int message, nint info, nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is not WimFile file)
        {
            return WimLibNative.ProgressContinue;
        }

        try
        {
            file._progress?.Invoke(message, info);
        }
        catch (Exception)
        {
            return WimLibNative.ProgressAbort;
        }

        return file._cancellation.IsCancellationRequested ? WimLibNative.ProgressAbort : WimLibNative.ProgressContinue;
    }

    private static void EnsureInitialised()
    {
        lock (InitGate)
        {
            if (_initialised)
            {
                return;
            }

            ThrowIfFailed(WimLibNative.GlobalInit(0), "initialise wimlib");
            _initialised = true;
        }
    }

    private static void ThrowIfCanceled(CancellationToken token) => token.ThrowIfCancellationRequested();

    private static void ThrowIfFailed(int code, string action)
    {
        if (code == 0)
        {
            return;
        }

        var message = NativeString.Read(WimLibNative.GetErrorString(code)) ?? $"error {code}";
        throw new BootrixException(ErrorCode.ExternalToolFailed, $"wimlib: {action}: {message} ({code})")
        {
            Arguments = ["wimlib", $"{code}: {message}"],
        };
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);
}
