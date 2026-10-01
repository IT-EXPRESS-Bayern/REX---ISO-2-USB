// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;
using Bootrix.Windows.Platform;

namespace Bootrix.Windows.Dism;

/// <summary>
/// Takes over and deletes files that belong to TrustedInstaller. The administrators group is
/// addressed by its well-known SID, so this works on every Windows language; the command line
/// tools do not (their group name and confirmation letters are localized).
/// </summary>
public static class FileOwnership
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void DeleteTree(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        try
        {
            DeleteCore(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Privileges.Enable(Privileges.TakeOwnership, Privileges.Restore, Privileges.Backup);
            TakeOwnership(path);
            DeleteCore(path);
        }
    }

    /// <summary>Makes the administrators group owner of the entry and everything below it and grants it full control.</summary>
    public static void TakeOwnership(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var isDirectory = Directory.Exists(current);
            GrantFullControl(current, isDirectory);

            if (isDirectory && (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void GrantFullControl(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.SetOwner(Administrators);
            info.SetAccessControl(security);

            security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                Administrators,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetOwner(Administrators);
            info.SetAccessControl(security);

            security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    private static void DeleteCore(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // A junction or symbolic link: remove the link itself, never what it points to.
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteCore(child);
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: false);
    }
}
