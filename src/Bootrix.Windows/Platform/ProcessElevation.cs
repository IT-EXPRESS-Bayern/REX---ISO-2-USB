// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Platform;

public static class ProcessElevation
{
    /// <summary>
    /// True when the process runs with the full administrator token. A member of the Administrators
    /// group that was not started "as administrator" gets false: UAC hands it a filtered token.
    /// </summary>
    public static unsafe bool IsElevated()
    {
        if (!Advapi32.OpenProcessToken(Kernel32Process.GetCurrentProcess(), Advapi32.TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            TokenInformation.Elevation elevation = default;
            if (!TokenInformation.GetTokenInformation(token, TokenInformation.TokenElevation, &elevation, (uint)sizeof(TokenInformation.Elevation), out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return elevation.TokenIsElevated != 0;
        }
    }

    /// <summary>The SID of the account the process runs as, in the S-1-5-21-... form.</summary>
    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
    }
}
