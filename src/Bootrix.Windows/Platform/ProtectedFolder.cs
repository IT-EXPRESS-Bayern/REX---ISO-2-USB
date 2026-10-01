// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bootrix.Core.Errors;

namespace Bootrix.Windows.Platform;

public enum FolderTrust
{
    Trusted,

    /// <summary>Owned by a trusted account, but someone else may write to it. Can be repaired.</summary>
    LooseAccess,

    /// <summary>Owned by someone who can change the access rules at will. Cannot be repaired by changing the rules.</summary>
    ForeignOwner,

    ReparsePoint,
}

/// <summary>An allow or deny entry of a folder, reduced to what the trust check looks at.</summary>
public readonly record struct FolderRule(SecurityIdentifier Identity, FileSystemRights Rights, bool Allow);

/// <summary>
/// Folders in which the elevated process keeps files (work area, logs, downloaded tools) have to be out of reach of
/// standard users: whoever can write there could swap a file or plant a junction and make the elevated process act on it.
/// A folder is only used when SYSTEM or administrators own it and nobody else may write to it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedFolder
{
    private const FileSystemRights Writing = FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// Makes sure <paramref name="path"/> exists and is protected. Below ProgramData the folders between ProgramData and
    /// <paramref name="path"/> are checked as well, because whoever owns a parent can rename the folder away and put another in its place.
    /// </summary>
    /// <param name="usersMayRead">Standard users may read the contents of the last folder (tools they are allowed to run); the parents stay closed.</param>
    /// <exception cref="BootrixException">With <see cref="ErrorCode.WorkspaceUntrusted"/> when an existing folder cannot be trusted.</exception>
    public static void Ensure(string path, bool usersMayRead = false)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        foreach (var folder in ChainTo(full))
        {
            EnsureOne(folder, usersMayRead && string.Equals(folder, full, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static FolderTrust Evaluate(bool isReparsePoint, SecurityIdentifier? owner, IEnumerable<FolderRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (isReparsePoint)
        {
            return FolderTrust.ReparsePoint;
        }

        if (owner is null || !IsTrusted(owner))
        {
            return FolderTrust.ForeignOwner;
        }

        foreach (var rule in rules)
        {
            if (rule.Allow && (rule.Rights & Writing) != 0 && !IsTrusted(rule.Identity))
            {
                return FolderTrust.LooseAccess;
            }
        }

        return FolderTrust.Trusted;
    }

    private static List<string> ChainTo(string full)
    {
        var common = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).TrimEnd('\\');
        var chain = new List<string>();
        if (full.StartsWith(common + '\\', StringComparison.OrdinalIgnoreCase))
        {
            for (var current = full; current is not null && current.Length > common.Length; current = Path.GetDirectoryName(current))
            {
                chain.Add(current);
            }

            chain.Reverse();
        }
        else
        {
            chain.Add(full);
        }

        return chain;
    }

    private static void EnsureOne(string folder, bool usersMayRead)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists)
        {
            try
            {
                info.Create(CreateSecurity(usersMayRead));
                info.Refresh();
            }
            catch (IOException) when (Directory.Exists(folder))
            {
                // Somebody else made it first; it is checked like any existing folder.
                info.Refresh();
            }
        }

        switch (Inspect(info))
        {
            case FolderTrust.Trusted:
                return;
            case FolderTrust.LooseAccess:
                info.SetAccessControl(CreateSecurity(usersMayRead));
                if (Inspect(info) == FolderTrust.Trusted)
                {
                    return;
                }

                break;
        }

        throw new BootrixException(ErrorCode.WorkspaceUntrusted, folder) { Arguments = [folder] };
    }

    private static FolderTrust Inspect(DirectoryInfo info)
    {
        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new FolderRule((SecurityIdentifier)rule.IdentityReference, rule.FileSystemRights, rule.AccessControlType == AccessControlType.Allow));

        return Evaluate(
            info.Attributes.HasFlag(FileAttributes.ReparsePoint),
            security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
            rules);
    }

    private static bool IsTrusted(SecurityIdentifier sid) => sid.Equals(System) || sid.Equals(Administrators) || sid.Equals(TrustedInstaller);

    private static DirectorySecurity CreateSecurity(bool usersMayRead)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersMayRead)
        {
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
