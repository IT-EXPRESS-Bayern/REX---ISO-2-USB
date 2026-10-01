// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Platform;

/// <summary>
/// Confirms that a program carries a valid Authenticode signature of Microsoft before Bootrix
/// runs it with administrator rights. A file picked up from a user-writable location, an ISO or a
/// USB stick must never be started on trust.
/// </summary>
public static class AuthenticodeVerifier
{
    public static bool IsSignedByMicrosoft(string path)
    {
        return HasValidSignature(path) && SignerSubject(path)?.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool HasValidSignature(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathPointer = Marshal.StringToHGlobalUni(fullPath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrust.FileInfo>());
        try
        {
            var fileInfo = new WinTrust.FileInfo { Size = (uint)Marshal.SizeOf<WinTrust.FileInfo>(), FilePath = pathPointer };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var data = new WinTrust.Data
            {
                Size = (uint)Marshal.SizeOf<WinTrust.Data>(),
                UiChoice = WinTrust.UiNone,
                RevocationChecks = WinTrust.RevokeNone,
                UnionChoice = WinTrust.ChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrust.StateVerify,
                ProvFlags = WinTrust.ProvFlagsRevocationCheckNone,
            };

            var action = WinTrust.GenericVerifyV2;
            var result = WinTrust.WinVerifyTrust(-1, ref action, ref data);

            data.StateAction = WinTrust.StateClose;
            WinTrust.WinVerifyTrust(-1, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    public static string? SignerSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only API that reads an embedded Authenticode certificate.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
