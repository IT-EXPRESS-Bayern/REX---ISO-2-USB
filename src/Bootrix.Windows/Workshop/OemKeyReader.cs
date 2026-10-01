// SPDX-License-Identifier: GPL-3.0-or-later
using System.Management;
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Licensing;

namespace Bootrix.Windows.Workshop;

internal static class OemKeyReader
{
    private const string TableSignature = "MSDM";

    public static OemLicenseInfo Read(IssueLog issues)
    {
        var table = FirmwareTables.Read(FirmwareTables.AcpiProvider, FirmwareTables.AcpiTableId(TableSignature));
        if (table is null)
        {
            return new OemLicenseInfo { HasFirmwareKey = false };
        }

        var msdm = MsdmParser.Parse(table);
        if (msdm?.ProductKey is null)
        {
            // A table that is there but holds no readable key: say so in the issues instead of claiming the PC has no license.
            issues.Add("msdm", new InvalidDataException("The MSDM table is present but contains no readable product key."));
            return new OemLicenseInfo { HasFirmwareKey = false, TableOemId = msdm?.OemId };
        }

        var description = Oa3Description.Parse(ReadDescription(issues));
        return new OemLicenseInfo
        {
            HasFirmwareKey = true,
            MaskedKey = msdm.MaskedKey,
            PlainKey = msdm.ProductKey,
            EditionDescription = description?.Raw,
            EditionId = description?.EditionId,
            Channel = description?.Channel,
            TableOemId = msdm.OemId,
        };
    }

    /// <summary>OA3xOriginalProductKeyDescription is not part of the documented SoftwareLicensingService class, so it can be missing or empty.</summary>
    private static string? ReadDescription(IssueLog issues)
    {
        try
        {
            return Wmi.Select("SELECT OA3xOriginalProductKeyDescription FROM SoftwareLicensingService", s => Wmi.GetString(s, "OA3xOriginalProductKeyDescription"))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            issues.Add("oa3-description", ex);
            return null;
        }
    }
}
