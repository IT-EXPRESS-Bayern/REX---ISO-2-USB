// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Workshop.Firmware;

/// <summary>
/// Looks for a certificate in the raw contents of the Secure Boot variables db and dbx. A full EFI_SIGNATURE_LIST parser
/// belongs to the Secure Boot status feature; here the subject name inside the DER encoding is enough to tell which
/// boot manager certificate the firmware trusts, which is the same test Microsoft's guidance scripts use.
/// </summary>
public static class SecureBootDatabase
{
    public const string WindowsUefiCa2023 = "Windows UEFI CA 2023";
    public const string WindowsProductionPca2011 = "Microsoft Windows Production PCA 2011";

    /// <summary>Variable namespace GUID of db and dbx (EFI_IMAGE_SECURITY_DATABASE_GUID).</summary>
    public const string DatabaseGuid = "{d719b2cb-3d3a-4596-a3bc-dad00e67656f}";

    /// <summary>Variable namespace GUID of the UEFI global variables such as SecureBoot.</summary>
    public const string GlobalGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    public static bool ContainsCertificate(ReadOnlySpan<byte> variable, string subjectName) =>
        variable.IndexOf(Encoding.ASCII.GetBytes(subjectName)) >= 0;
}
