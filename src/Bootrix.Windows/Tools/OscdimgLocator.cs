// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Bootrix.Core.Errors;
using Bootrix.Windows.Platform;
using Microsoft.Win32;

namespace Bootrix.Windows.Tools;

/// <summary>
/// Finds oscdimg.exe, the tool that writes bootable Windows ISOs. It belongs to the Windows ADK and
/// may not be redistributed, so it is taken from an installed ADK or downloaded from Microsoft's
/// symbol server (the same file the ADK ships), checked against a pinned hash and a Microsoft
/// signature, and kept in a folder that only administrators can write to.
/// </summary>
public sealed class OscdimgLocator(HttpClient? http = null)
{
    private const string DownloadUrl = "https://msdl.microsoft.com/download/symbols/oscdimg.exe/3D44737265000/oscdimg.exe";

    // oscdimg.exe 10.0.x as shipped on the Microsoft symbol server (x64).
    private const string DownloadSha256 = "f5129f313ed7eb46f2677cf522e64264a225f226307ed0ddb52bb14c46e7cfdd";

    private readonly HttpClient _http = http ?? new HttpClient();

    public static string ToolFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bootrix", "tools");

    public async Task<string> GetPathAsync(CancellationToken cancellationToken)
    {
        var installed = FindInstalled();
        if (installed is not null)
        {
            return installed;
        }

        var cached = Path.Combine(ToolFolder, "oscdimg.exe");
        if (File.Exists(cached) && IsTrusted(cached))
        {
            return cached;
        }

        return await DownloadAsync(cached, cancellationToken).ConfigureAwait(false);
    }

    public static string? FindInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            if (key?.GetValue("KitsRoot10") is not string root)
            {
                continue;
            }

            var candidate = Path.Combine(root, "Assessment and Deployment Kit", "Deployment Tools", AdkArchitectureFolder(), "Oscdimg", "oscdimg.exe");
            if (File.Exists(candidate) && AuthenticodeVerifier.IsSignedByMicrosoft(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string AdkArchitectureFolder() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "amd64",
    };

    private static bool IsTrusted(string path) => AuthenticodeVerifier.IsSignedByMicrosoft(path);

    private async Task<string> DownloadAsync(string destination, CancellationToken cancellationToken)
    {
        EnsureProtectedFolder(ToolFolder);
        var temporary = destination + ".download";

        try
        {
            await using (var response = await _http.GetStreamAsync(DownloadUrl, cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false)));
            if (!string.Equals(hash, DownloadSha256, StringComparison.Ordinal))
            {
                throw new BootrixException(ErrorCode.DownloadHashMismatch, $"oscdimg.exe hash {hash}");
            }

            if (!AuthenticodeVerifier.IsSignedByMicrosoft(temporary))
            {
                throw new BootrixException(ErrorCode.ExternalToolUntrusted, "oscdimg.exe") { Arguments = ["oscdimg.exe"] };
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Only SYSTEM and administrators may change files in the tool folder; a normal user could otherwise swap the program that runs elevated.</summary>
    internal static void EnsureProtectedFolder(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
