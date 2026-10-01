// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;

namespace Bootrix.Windows.Optical;

/// <summary>
/// IMAPI returns lists (volume paths, profiles, write speeds) as SAFEARRAYs of VARIANT. The generated
/// interfaces expose them as raw pointers, so reading and building them is done here, once.
/// </summary>
internal static unsafe class SafeArrays
{
    /// <summary>Reads a one-dimensional array into objects and destroys it; returns an empty list for null.</summary>
    public static List<object?> Read(SAFEARRAY* array)
    {
        var result = new List<object?>();
        if (array is null)
        {
            return result;
        }

        try
        {
            if (PInvoke.SafeArrayGetDim(array) != 1)
            {
                return result;
            }

            PInvoke.SafeArrayGetVartype(array, out var type);
            PInvoke.SafeArrayGetLBound(array, 1, out var lower);
            PInvoke.SafeArrayGetUBound(array, 1, out var upper);
            var count = upper - lower + 1;
            if (count <= 0)
            {
                return result;
            }

            PInvoke.SafeArrayAccessData(array, out var data).ThrowOnFailure();
            try
            {
                var elementSize = (int)array->cbElements;
                for (var i = 0; i < count; i++)
                {
                    var element = (byte*)data + i * elementSize;
                    result.Add(ReadElement(type, element));
                }
            }
            finally
            {
                PInvoke.SafeArrayUnaccessData(array);
            }
        }
        finally
        {
            PInvoke.SafeArrayDestroy(array);
        }

        return result;
    }

    public static List<int> ReadInts(SAFEARRAY* array) =>
        [.. Read(array).Select(e => e is null ? 0 : Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture))];

    public static List<string> ReadStrings(SAFEARRAY* array) =>
        [.. Read(array).OfType<string>()];

    /// <summary>Builds a SAFEARRAY of VARIANT holding the given COM objects (as VT_DISPATCH). The caller destroys it after the call that takes it.</summary>
    public static SAFEARRAY* CreateObjectVector(IReadOnlyList<object> items)
    {
        var array = PInvoke.SafeArrayCreateVector(VARENUM.VT_VARIANT, 0, (uint)items.Count);
        if (array is null)
        {
            throw new InvalidOperationException("SafeArrayCreateVector failed");
        }

        try
        {
            PInvoke.SafeArrayAccessData(array, out var data).ThrowOnFailure();
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    Marshal.GetNativeVariantForObject(items[i], (nint)((byte*)data + i * (int)array->cbElements));
                }
            }
            finally
            {
                PInvoke.SafeArrayUnaccessData(array);
            }
        }
        catch
        {
            PInvoke.SafeArrayDestroy(array);
            throw;
        }

        return array;
    }

    public static void Destroy(SAFEARRAY* array)
    {
        if (array is not null)
        {
            PInvoke.SafeArrayDestroy(array);
        }
    }

    private static object? ReadElement(VARENUM type, byte* element) => type switch
    {
        VARENUM.VT_VARIANT => Marshal.GetObjectForNativeVariant((nint)element),
        VARENUM.VT_BSTR => Marshal.PtrToStringBSTR(*(nint*)element),
        VARENUM.VT_I4 or VARENUM.VT_INT => *(int*)element,
        VARENUM.VT_UI4 or VARENUM.VT_UINT => (int)*(uint*)element,
        VARENUM.VT_DISPATCH or VARENUM.VT_UNKNOWN => *(nint*)element == 0 ? null : Marshal.GetObjectForIUnknown(*(nint*)element),
        _ => null,
    };
}
