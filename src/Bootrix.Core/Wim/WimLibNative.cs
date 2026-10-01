// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bootrix.Core.Wim;

/// <summary>
/// Thin binding to wimlib (LGPL). The library takes wchar_t paths on Windows and UTF-8 on other
/// systems, so paths travel as raw pointers produced by <see cref="NativeString"/>.
/// </summary>
internal static unsafe partial class WimLibNative
{
    public const string Library = "wim";

    public const int CompressionNone = 0;
    public const int CompressionXpress = 1;
    public const int CompressionLzx = 2;
    public const int CompressionLzms = 3;

    public const int WriteCheckIntegrity = 0x1;
    public const int WriteRecompress = 0x10;
    public const int WriteSolid = 0x1000;

    public const int OpenCheckIntegrity = 0x1;
    public const int OpenWriteAccess = 0x4;

    public const int ExtractNoAcls = 0x40;
    public const int ExtractGlobPaths = 0x40000;
    public const int ExtractNoPreserveDirStructure = 0x200000;

    public const int ProgressContinue = 0;
    public const int ProgressAbort = 1;

    public const int MsgWriteStreams = 12;
    public const int MsgSplitBeginPart = 19;
    public const int MsgSplitEndPart = 20;

    public const int AllImages = -1;

    public const int InfoSize = 88;

    static WimLibNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(WimLibNative).Assembly, Resolve);
    }

    internal static string[] CandidateNames()
    {
        var overridePath = Environment.GetEnvironmentVariable("BOOTRIX_WIMLIB");
        var names = new List<string>();
        if (!string.IsNullOrEmpty(overridePath))
        {
            names.Add(overridePath);
        }

        if (OperatingSystem.IsWindows())
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            names.Add(Path.Combine(AppContext.BaseDirectory, "libwim-15.dll"));
            names.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", "libwim-15.dll"));
            names.Add("libwim-15.dll");
        }
        else
        {
            names.Add("libwim.so.15");
            names.Add("libwim.so");
            names.Add("libwim.15.dylib");
        }

        return [.. names];
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
        {
            return 0;
        }

        foreach (var candidate in CandidateNames())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }

    public static bool IsAvailable()
    {
        foreach (var candidate in CandidateNames())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    [LibraryImport(Library, EntryPoint = "wimlib_global_init")]
    public static partial int GlobalInit(int flags);

    [LibraryImport(Library, EntryPoint = "wimlib_open_wim")]
    public static partial int OpenWim(nint path, int flags, out nint wim);

    [LibraryImport(Library, EntryPoint = "wimlib_free")]
    public static partial void Free(nint wim);

    [LibraryImport(Library, EntryPoint = "wimlib_get_wim_info")]
    public static partial int GetWimInfo(nint wim, byte* info);

    [LibraryImport(Library, EntryPoint = "wimlib_get_image_property")]
    public static partial nint GetImageProperty(nint wim, int image, nint propertyName);

    [LibraryImport(Library, EntryPoint = "wimlib_split")]
    public static partial int Split(nint wim, nint swmName, ulong partSize, int writeFlags);

    [LibraryImport(Library, EntryPoint = "wimlib_write")]
    public static partial int Write(nint wim, nint path, int image, int writeFlags, uint threads);

    [LibraryImport(Library, EntryPoint = "wimlib_create_new_wim")]
    public static partial int CreateNewWim(int compressionType, out nint wim);

    [LibraryImport(Library, EntryPoint = "wimlib_export_image")]
    public static partial int ExportImage(nint source, int sourceImage, nint destination, nint name, nint description, int flags);

    [LibraryImport(Library, EntryPoint = "wimlib_set_output_compression_type")]
    public static partial int SetOutputCompressionType(nint wim, int compressionType);

    [LibraryImport(Library, EntryPoint = "wimlib_extract_paths")]
    public static partial int ExtractPaths(nint wim, int image, nint target, nint* paths, nuint pathCount, int flags);

    [LibraryImport(Library, EntryPoint = "wimlib_register_progress_function")]
    public static partial void RegisterProgressFunction(nint wim, delegate* unmanaged<int, nint, nint, int> function, nint context);

    [LibraryImport(Library, EntryPoint = "wimlib_get_error_string")]
    public static partial nint GetErrorString(int code);
}

/// <summary>A native string in the encoding wimlib expects on the current platform.</summary>
internal readonly unsafe struct NativeString : IDisposable
{
    private readonly nint _pointer;

    public NativeString(string? text)
    {
        _pointer = text is null
            ? 0
            : OperatingSystem.IsWindows() ? Marshal.StringToHGlobalUni(text) : Marshal.StringToCoTaskMemUTF8(text);
    }

    public nint Pointer => _pointer;

    public static string? Read(nint pointer)
    {
        if (pointer == 0)
        {
            return null;
        }

        return OperatingSystem.IsWindows() ? Marshal.PtrToStringUni(pointer) : Marshal.PtrToStringUTF8(pointer);
    }

    public void Dispose()
    {
        if (_pointer == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Marshal.FreeHGlobal(_pointer);
        }
        else
        {
            Marshal.FreeCoTaskMem(_pointer);
        }
    }
}
