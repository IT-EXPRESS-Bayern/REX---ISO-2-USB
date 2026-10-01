// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Engine;

/// <summary>
/// Syntax checks for paths that arrive from the unprivileged side. They follow Windows rules whatever
/// system they run on, because the broker is a Windows process; they only look at the text and never touch the file system.
/// </summary>
internal static partial class EnginePathRules
{
    public const string DiskInterfaceGuid = "53f56307-b6bf-11d0-94f2-00a0c91efb8b";

    private const int MaxPathLength = 32_767;
    private const int MaxDevicePathLength = 1024;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A file path in the drive form (C:\dir\file.iso), the same with the \\?\ prefix, or a network
    /// path (\\server\share\file.iso). Device paths such as \\.\PhysicalDrive0, \\?\GLOBALROOT\...,
    /// pipes, reserved names (NUL, COM1), relative paths and "..", alternate streams are all rejected.
    /// </summary>
    public static bool IsAbsoluteFilePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || ContainsForbiddenCharacter(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return IsNetworkPath(path[8..]);
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var drivePath = path[4..];
            return HasDriveRoot(drivePath) && AreSafeComponents(drivePath[3..]);
        }

        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return IsNetworkPath(path[2..]);
        }

        return HasDriveRoot(path) && AreSafeComponents(path[3..]);
    }

    /// <summary>
    /// A device interface path as SetupAPI reports it: \\?\ followed by the instance name with '#'
    /// as separator, ending in the interface class GUID. Anything with a drive letter, a share or a further backslash is not one.
    /// </summary>
    public static bool IsDeviceInterfacePath(string? path, string interfaceGuid)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxDevicePathLength || !path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return false;
        }

        var name = path[4..];
        return DeviceInterfaceName().IsMatch(name)
            && name.EndsWith("#{" + interfaceGuid + "}", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("UNC#", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("GLOBALROOT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsForbiddenCharacter(string path)
    {
        // The '?' of the extended prefix is the only one allowed; the prefix is cut off before the rest is looked at.
        var body = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.AsSpan(4) : path.AsSpan();
        foreach (var c in body)
        {
            if (c < ' ' || c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDriveRoot(string path) =>
        path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>Server, share and at least one more component, all of them harmless.</summary>
    private static bool IsNetworkPath(string afterPrefix)
    {
        var parts = afterPrefix.Split('\\', '/');
        return parts.Length >= 3
            && parts[0].Length > 0
            && parts[0] is not ("." or "?")
            && AreSafeComponents(string.Join('\\', parts.Skip(1)))
            && IsSafeComponent(parts[0]);
    }

    private static bool AreSafeComponents(string relative)
    {
        if (relative.Length == 0)
        {
            return false;
        }

        foreach (var component in relative.Split('\\', '/'))
        {
            if (!IsSafeComponent(component))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeComponent(string component)
    {
        if (component.Length == 0 || component is "." or "..")
        {
            return false;
        }

        // Windows silently drops trailing dots and spaces, which would make two spellings of one name.
        if (component[^1] is '.' or ' ' || component.Contains(':'))
        {
            return false;
        }

        var dot = component.IndexOf('.', StringComparison.Ordinal);
        var baseName = (dot < 0 ? component : component[..dot]).TrimEnd(' ');
        return !ReservedNames.Contains(baseName);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_&.\-{}#@$()+,;=]+\z")]
    private static partial Regex DeviceInterfaceName();
}
