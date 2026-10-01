// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Images.Policy;

/// <summary>
/// The warnings that go with a decision: what a raw copy of a read-only ISO layout does to the stick, which firmware
/// quirks apply to hybrid images, what extraction costs on FAT, NTFS and exFAT, and the notes the family data adds.
/// </summary>
internal sealed class PolicyWarnings(
    ImageProfile profile,
    FamilyPolicy? family,
    PolicyRequest request,
    PolicyMode mode,
    bool optical,
    bool labelPatched,
    bool persistencePossible)
{
    private readonly List<ImageWarning> _warnings = [];

    private bool HasFlag(string flag) => family?.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase) == true;

    private bool IsWindows => profile.Kind is ImageKind.WindowsSetup or ImageKind.WindowsPe;

    public List<ImageWarning> Collect()
    {
        if (mode == PolicyMode.RawCopy)
        {
            RawCopyWarnings();
        }
        else if (mode == PolicyMode.Extract)
        {
            ExtractWarnings();
        }

        SizeWarnings();
        PersistenceWarnings();

        if (profile.Kind == ImageKind.Bsd && optical)
        {
            Add(ImagePolicyKeys.BsdIsoForOptical);
        }

        foreach (var key in family?.Warnings ?? [])
        {
            Add(key);
        }

        return [.. _warnings.DistinctBy(warning => warning.Key)];
    }

    private void Add(string key, WarningSeverity severity = WarningSeverity.Warning, params object?[] arguments) =>
        _warnings.Add(new ImageWarning(key, severity, arguments));

    private void RawCopyWarnings()
    {
        if (optical)
        {
            Add(ImagePolicyKeys.DdReadOnlyLayout, WarningSeverity.Info);
            if (profile.HasGpt)
            {
                Add(ImagePolicyKeys.GptBackupAtIsoEnd, WarningSeverity.Info);
            }

            if (profile.HasElToritoEfi && !profile.HasEspPartition)
            {
                Add(ImagePolicyKeys.UefiOnlyEdk2);
            }

            if (profile.HasGpt && profile.HasProtectiveMbr)
            {
                Add(ImagePolicyKeys.GptProtectiveMbrLegacy);
            }
        }

        if (request.TargetSectorSize > 512 && (profile.IsHybrid || profile.Container == ImageContainer.FatVolume))
        {
            Add(ImagePolicyKeys.Sector4kTarget);
        }

        if (profile.Container == ImageContainer.FatVolume)
        {
            Add(ImagePolicyKeys.SuperfloppyUnreliable);
        }

        if (profile.IsCompressed && profile.ImageBytes == 0)
        {
            Add(ImageWarningKeys.CompressedSizeUnknown, WarningSeverity.Info);
        }
    }

    private void ExtractWarnings()
    {
        if (labelPatched && profile.VolumeLabel is { } label)
        {
            Add(ImagePolicyKeys.LabelChanged, WarningSeverity.Info, label, FatLabel.ToValid(label));
        }

        // Windows setup media are handled by their own writer, which splits install.wim for FAT32.
        if (!IsWindows && profile.HasFileOver4GiB && request.TargetFileSystem is FileSystemKind.Auto or FileSystemKind.Fat32 or FileSystemKind.Fat16 or FileSystemKind.Fat12)
        {
            Add(ImagePolicyKeys.FatFileLimit);
        }

        if (request.TargetFileSystem is FileSystemKind.Ntfs or FileSystemKind.ExFat && !IsWindows)
        {
            Add(ImagePolicyKeys.NtfsExfatHelper);
        }

        if (HasFlag("casper") && request.TargetFileSystem == FileSystemKind.ExFat)
        {
            Add(ImagePolicyKeys.CasperExfat);
        }

        if (profile.Kind is ImageKind.LinuxHybrid or ImageKind.LinuxIsoOnly)
        {
            Add(ImagePolicyKeys.BootloaderOffline, WarningSeverity.Info);
        }

        if (profile.HasEmulatedBootImage)
        {
            Add(ImagePolicyKeys.LegacyBiosOnly);
        }
    }

    private void SizeWarnings()
    {
        if (request.TargetSizeBytes is not { } target)
        {
            return;
        }

        var needed = mode == PolicyMode.Extract ? profile.TotalBytes : profile.ImageBytes;
        if (needed > target)
        {
            Add(ImagePolicyKeys.ImageLargerThanTarget, WarningSeverity.Error, needed, target);
        }
    }

    private void PersistenceWarnings()
    {
        if (!request.WantsPersistence)
        {
            return;
        }

        if (!persistencePossible)
        {
            Add(ImagePolicyKeys.PersistenceUnsupported);
        }
        else if (mode == PolicyMode.RawCopy && HasFlag("liveBoot"))
        {
            Add(ImagePolicyKeys.PersistenceBootArgument, WarningSeverity.Info);
        }
    }
}
