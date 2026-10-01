// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop.Advice;

/// <summary>Chooses how the medium has to boot on this PC: firmware mode, partition scheme and what Secure Boot demands of it.</summary>
internal static class BootAdvisor
{
    /// <summary>An MBR cannot address more than 2 TiB, which limits what a BIOS system can use of a disk.</summary>
    private const long MbrLimitBytes = 2L << 40;

    public static BootRecommendation Advise(TargetPcInfo info)
    {
        var firmware = info.Firmware?.Type ?? FirmwareType.Unknown;
        if (firmware == FirmwareType.Unknown && info.Cpu?.Architecture == CpuArchitecture.Arm64)
        {
            firmware = FirmwareType.Uefi;
        }

        var notes = new List<AdvisorMessage>();
        var recommendation = firmware switch
        {
            FirmwareType.Uefi => new BootRecommendation { Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Gpt },
            FirmwareType.Bios => new BootRecommendation { Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Mbr },
            _ => new BootRecommendation(),
        };

        if (firmware == FirmwareType.Bios)
        {
            notes.Add(new AdvisorMessage(AdvisorKeys.BootBiosMode, AdvisorSeverity.Info));
            var large = info.Disks?.Where(d => d.IsInternal && d.SizeBytes > MbrLimitBytes).Select(d => d.Name).FirstOrDefault();
            if (large is not null)
            {
                notes.Add(new AdvisorMessage(AdvisorKeys.BootBiosLargeDisk, AdvisorSeverity.Warning, large));
            }

            return recommendation with { Notes = notes };
        }

        if (firmware != FirmwareType.Uefi)
        {
            return recommendation;
        }

        var secureBoot = info.Firmware?.SecureBoot ?? SecureBootState.Unknown;
        recommendation = recommendation with
        {
            SecureBootEnabled = secureBoot switch
            {
                SecureBootState.On => true,
                SecureBootState.Off => false,
                _ => null,
            },
        };

        if (secureBoot != SecureBootState.On)
        {
            return recommendation with { RequiresSignedMedia = secureBoot == SecureBootState.Off ? false : null };
        }

        // With Secure Boot on, FAT32 and Microsoft's own boot manager need no additional trust; NTFS would need a third-party UEFI loader.
        notes.Add(new AdvisorMessage(AdvisorKeys.BootSecureBootSignedOnly, AdvisorSeverity.Info));
        var certificate = ChooseCertificate(info.SecureBootCertificates, notes);
        return recommendation with
        {
            RequiresSignedMedia = true,
            FileSystem = FileSystemKind.Fat32,
            Certificate = certificate,
            Notes = notes,
        };
    }

    private static BootCertificate ChooseCertificate(SecureBootCertificateInfo? certificates, List<AdvisorMessage> notes)
    {
        if (certificates is null)
        {
            return BootCertificate.Auto;
        }

        if (certificates.Windows2011RevokedInDbx == true && certificates.Windows2023InDb != false)
        {
            notes.Add(new AdvisorMessage(AdvisorKeys.BootCertificate2023, AdvisorSeverity.Warning));
            return BootCertificate.Windows2023;
        }

        if (certificates.Windows2023InDb == false && certificates.Windows2011InDb == true)
        {
            notes.Add(new AdvisorMessage(AdvisorKeys.BootCertificate2011, AdvisorSeverity.Info));
            return BootCertificate.Windows2011;
        }

        return BootCertificate.Auto;
    }
}
