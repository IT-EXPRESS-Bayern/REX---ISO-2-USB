// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Platform;

/// <summary>Enables token privileges that an administrator has but that are switched off by default.</summary>
public static class Privileges
{
    public const string Backup = "SeBackupPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";
    public const string SystemEnvironment = "SeSystemEnvironmentPrivilege";

    public static void Enable(params string[] names)
    {
        if (!Advapi32.OpenProcessToken(Kernel32Process.GetCurrentProcess(), Advapi32.TokenAdjustPrivileges | Advapi32.TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            foreach (var name in names)
            {
                if (!Advapi32.LookupPrivilegeValue(null, name, out var luid))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Unknown privilege {name}");
                }

                var state = new Advapi32.TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = Advapi32.SePrivilegeEnabled };
                if (!Advapi32.AdjustTokenPrivileges(token, false, ref state, 0, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                // AdjustTokenPrivileges succeeds even when the privilege is not held; the real answer is in the last error.
                if (Marshal.GetLastPInvokeError() == Advapi32.ErrorNotAllAssigned)
                {
                    throw new InvalidOperationException($"The process does not hold {name}; it must run elevated.");
                }
            }
        }
    }
}
