// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Interop;

internal static unsafe class DeviceIo
{
    public static SafeFileHandle Open(string path, uint access, uint share, uint flags = 0)
    {
        var handle = Kernel32.CreateFile(path, access, share, 0, Kernel32.OpenExisting, flags, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open {path}");
        }

        return handle;
    }

    public static SafeFileHandle? TryOpen(string path, uint access, uint share, uint flags = 0)
    {
        var handle = Kernel32.CreateFile(path, access, share, 0, Kernel32.OpenExisting, flags, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    /// <summary>Opens a device only to query it. No access rights are requested, so this works for any caller and does not spin up the media.</summary>
    public static SafeFileHandle? OpenForQuery(string path) =>
        TryOpen(path, 0, Kernel32.FileShareRead | Kernel32.FileShareWrite);

    public static bool TryControl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int returned, out int error)
    {
        fixed (byte* inPointer = input)
        fixed (byte* outPointer = output)
        {
            var ok = Kernel32.DeviceIoControl(
                handle,
                code,
                inPointer,
                (uint)input.Length,
                outPointer,
                (uint)output.Length,
                out var bytes,
                0);
            returned = (int)bytes;
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }

    public static int Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (!TryControl(handle, code, input, output, out var returned, out var error))
        {
            throw new Win32Exception(error, $"DeviceIoControl 0x{code:X8} failed");
        }

        return returned;
    }

    /// <summary>For control codes without payload, e.g. IOCTL_DISK_UPDATE_PROPERTIES or FSCTL_LOCK_VOLUME.</summary>
    public static void Control(SafeFileHandle handle, uint code) => Control(handle, code, [], []);

    public static bool TryControl(SafeFileHandle handle, uint code) => TryControl(handle, code, [], [], out _, out _);

    /// <summary>Runs a control code whose output size is not known in advance, doubling the buffer until it fits.</summary>
    public static byte[]? QueryGrowing(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, int initialSize = 1024, int maxSize = 4 * 1024 * 1024)
    {
        var size = initialSize;
        while (size <= maxSize)
        {
            var buffer = new byte[size];
            if (TryControl(handle, code, input, buffer, out var returned, out var error))
            {
                return buffer.AsSpan(0, returned).ToArray();
            }

            if (error is not (Kernel32.ErrorInsufficientBuffer or Kernel32.ErrorMoreData))
            {
                return null;
            }

            size *= 2;
        }

        return null;
    }

    public static T Query<T>(SafeFileHandle handle, uint code)
        where T : unmanaged
    {
        T value = default;
        var span = new Span<byte>(&value, sizeof(T));
        Control(handle, code, [], span);
        return value;
    }
}

internal sealed class ErrorModeScope : IDisposable
{
    private readonly uint _previous;

    public ErrorModeScope()
    {
        Kernel32.SetThreadErrorMode(Kernel32.SemFailCriticalErrors, out _previous);
    }

    public void Dispose() => Kernel32.SetThreadErrorMode(_previous, out _);
}
