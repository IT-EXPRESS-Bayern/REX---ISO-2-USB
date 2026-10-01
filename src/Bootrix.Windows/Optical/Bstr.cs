// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Bootrix.Windows.Optical;

/// <summary>Conversions for the BSTR strings IMAPI hands out and expects.</summary>
internal static unsafe class Bstr
{
    /// <summary>Copies a returned BSTR into a string and frees it; the caller owns every BSTR a property getter returns.</summary>
    public static string Take(BSTR value)
    {
        if (value.Value is null)
        {
            return string.Empty;
        }

        var text = Marshal.PtrToStringBSTR((nint)value.Value);
        PInvoke.SysFreeString(value);
        return text;
    }

    public static BstrScope Allocate(string text) => new(text);
}

/// <summary>A BSTR for an argument or property setter; the callee copies it, so it is freed when the scope ends.</summary>
internal readonly unsafe struct BstrScope : IDisposable
{
    private readonly nint _pointer;

    public BstrScope(string text) => _pointer = Marshal.StringToBSTR(text);

    public BSTR Value => new((char*)_pointer);

    public void Dispose() => Marshal.FreeBSTR(_pointer);
}
