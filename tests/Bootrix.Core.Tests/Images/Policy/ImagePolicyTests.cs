// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Model;

namespace Bootrix.Core.Tests.Images.Policy;

public class ImagePolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly ImagePolicy Policy = ImagePolicy.Default;

    /// <summary>A typical hybrid ISO with BIOS and EFI boot data and an EFI system partition.</summary>
    private static ImageProfile Hybrid(string? family, ImageKind kind = ImageKind.LinuxHybrid, string label = "LABEL") => new()
    {
        Kind = kind,
        Family = family,
        Container = ImageContainer.Iso9660,
        IsHybrid = true,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        HasElToritoBios = true,
        HasElToritoEfi = true,
        HasEspPartition = true,
        VolumeLabel = label,
        TotalBytes = 3 * GiB,
        ImageBytes = 3 * GiB,
    };

    private static ImageProfile IsoOnly(string? family, ImageKind kind) =>
        Hybrid(family, kind) with { IsHybrid = false, HasEspPartition = false };

    private static ImageProfile RawDisk(string? family, ImageKind kind = ImageKind.RawDisk) => new()
    {
        Kind = kind,
        Family = family,
        Container = ImageContainer.RawDisk,
        IsHybrid = true,
        HasEspPartition = true,
        ImageBytes = 4 * GiB,
        TotalBytes = 4 * GiB,
    };

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("linuxmint")]
    [InlineData("zorin")]
    [InlineData("elementary")]
    [InlineData("debian")]
    [InlineData("debian-live")]
    [InlineData("kali")]
    [InlineData("parrot")]
    [InlineData("clonezilla")]
    [InlineData("gparted")]
    [InlineData("fedora")]
    [InlineData("rhel")]
    [InlineData("arch")]
    [InlineData("endeavouros")]
    [InlineData("systemrescue")]
    public void HybridLinuxFamilies_DefaultToRawCopyAndAllowIsoMode(string family)
    {
        var decision = Policy.Evaluate(Hybrid(family));

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Equal([WriteMode.RawCopy, WriteMode.Extract], decision.AllowedModes);
        Assert.False(decision.IsForced);
        Assert.Equal(WriteMode.RawCopy, decision.WriteMode);
    }

    [Theory]
    [InlineData("manjaro")]
    [InlineData("popos")]
    [InlineData("proxmox")]
    [InlineData("opensuse")]
    [InlineData("truenas")]
    [InlineData("memtest86plus")]
    [InlineData("memtest86")]
    [InlineData("tails")]
    public void VendorMandatedFamilies_AreRawCopyOnly(string family)
    {
        var decision = Policy.Evaluate(Hybrid(family));

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Equal([WriteMode.RawCopy], decision.AllowedModes);
        Assert.True(decision.IsForced);
        Assert.Contains(ImagePolicyKeys.VendorMandatesRawCopy, decision.ReasonKeys);
    }

    [Theory]
    [InlineData("esxi")]
    [InlineData("reactos")]
    [InlineData("kolibrios")]
    [InlineData("freedos")]
    [InlineData("ms-dos")]
    public void IsoOnlyFamilies_AreExtractOnly(string family)
    {
        var decision = Policy.Evaluate(IsoOnly(family, ImageKind.OtherOs));

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Equal([WriteMode.Extract], decision.AllowedModes);
        Assert.Contains(ImagePolicyKeys.IsoModeOnly, decision.ReasonKeys);
    }

    [Theory]
    [InlineData("windows", ImageKind.WindowsSetup)]
    [InlineData("windows-nt5", ImageKind.WindowsSetup)]
    [InlineData("winpe", ImageKind.WindowsPe)]
    [InlineData("hirens-pe", ImageKind.WindowsPe)]
    [InlineData(null, ImageKind.WindowsSetup)]
    public void WindowsIsos_AreExtractedNeverRawCopied(string? family, ImageKind kind)
    {
        var decision = Policy.Evaluate(IsoOnly(family, kind));

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.True(decision.IsForced);
        Assert.Contains(ImagePolicyKeys.WindowsSetupExtract, decision.ReasonKeys);
    }

    [Fact]
    public void WindowsInstallerStickImage_IsWrittenRaw()
    {
        var decision = Policy.Evaluate(RawDisk("windows", ImageKind.WindowsSetup));

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Contains(ImagePolicyKeys.WindowsStickRawCopy, decision.ReasonKeys);
    }

    [Fact]
    public void WimFile_IsAppliedByTheWindowsLayer()
    {
        var profile = new ImageProfile { Kind = ImageKind.WindowsSetup, Container = ImageContainer.Wim };

        var decision = Policy.Evaluate(profile);

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Contains(ImagePolicyKeys.WimApplied, decision.ReasonKeys);
    }

    [Theory]
    [InlineData("raspberrypi", ImageKind.RawDisk)]
    [InlineData("freebsd", ImageKind.Bsd)]
    [InlineData("openbsd", ImageKind.Bsd)]
    [InlineData("chromeos", ImageKind.RawDisk)]
    [InlineData(null, ImageKind.RawDisk)]
    public void RawDiskImages_AreAlwaysRawCopied(string? family, ImageKind kind)
    {
        var decision = Policy.Evaluate(RawDisk(family, kind));

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Equal([WriteMode.RawCopy], decision.AllowedModes);
        Assert.Contains(ImagePolicyKeys.RawImageOnly, decision.ReasonKeys);
    }

    [Fact]
    public void FloppyImage_IsWrittenRawWithASuperfloppyWarning()
    {
        var profile = new ImageProfile { Kind = ImageKind.Dos, Family = "ms-dos", Container = ImageContainer.FatVolume, ImageBytes = 1_474_560 };

        var decision = Policy.Evaluate(profile);

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Contains(ImagePolicyKeys.FloppyImageRawCopy, decision.ReasonKeys);
        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.SuperfloppyUnreliable);
        Assert.Equal(BootSupport.No, decision.UefiBoot);
    }

    [Fact]
    public void UnknownHybridIso_DefaultsToRawCopy()
    {
        var decision = Policy.Evaluate(Hybrid(null));

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Contains(ImagePolicyKeys.HybridRawCopy, decision.ReasonKeys);
        Assert.Equal([WriteMode.RawCopy, WriteMode.Extract], decision.AllowedModes);
    }

    [Fact]
    public void UnknownNonHybridIso_CanOnlyBeExtracted()
    {
        var decision = Policy.Evaluate(IsoOnly(null, ImageKind.LinuxIsoOnly));

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Equal([WriteMode.Extract], decision.AllowedModes);
    }

    [Fact]
    public void NonHybridImageOfAHybridFamily_LosesTheRawCopyOption()
    {
        var decision = Policy.Evaluate(IsoOnly("ubuntu", ImageKind.LinuxIsoOnly));

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Equal([WriteMode.Extract], decision.AllowedModes);
        Assert.Contains(ImagePolicyKeys.NotBootableAsDisk, decision.ReasonKeys);
    }

    [Fact]
    public void DataIsoWithPartitionTable_AsksThePerson()
    {
        var profile = new ImageProfile { Kind = ImageKind.Data, Container = ImageContainer.Iso9660, IsHybrid = true };

        var decision = Policy.Evaluate(profile);

        Assert.Equal(PolicyMode.Ask, decision.Mode);
        Assert.Equal(WriteMode.Auto, decision.WriteMode);
        Assert.Equal([WriteMode.RawCopy, WriteMode.Extract], decision.AllowedModes);
        Assert.Contains(ImagePolicyKeys.DataImageAsk, decision.ReasonKeys);
    }

    [Fact]
    public void PlainDataIso_CanOnlyHaveItsFilesCopied()
    {
        var profile = new ImageProfile { Kind = ImageKind.Data, Container = ImageContainer.Iso9660 };

        var decision = Policy.Evaluate(profile);

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.True(decision.IsForced);
    }

    // ---- persistence -------------------------------------------------------------------------

    [Fact]
    public void Persistence_ForCasperFamilies_SwitchesToIsoMode()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu"), new PolicyRequest { WantsPersistence = true });

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.True(decision.PersistencePossible);
        Assert.Equal(PersistenceSupport.ExtractMode, decision.Persistence);
        Assert.Contains(ImagePolicyKeys.PersistenceNeedsExtract, decision.ReasonKeys);
    }

    [Fact]
    public void Persistence_ForLiveBootFamilies_KeepsRawCopyAndAddsAPartition()
    {
        var decision = Policy.Evaluate(Hybrid("kali"), new PolicyRequest { WantsPersistence = true });

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.True(decision.PersistencePossible);
        Assert.Equal(PersistenceSupport.RawCopyPartition | PersistenceSupport.ExtractMode, decision.Persistence);
        Assert.Contains(ImagePolicyKeys.PersistenceViaPartition, decision.ReasonKeys);
        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.PersistenceBootArgument);
    }

    [Theory]
    [InlineData("fedora")]
    [InlineData("arch")]
    [InlineData("popos")]
    [InlineData("manjaro")]
    public void Persistence_ForFamiliesWithoutSupport_IsRefusedWithAWarning(string family)
    {
        var decision = Policy.Evaluate(Hybrid(family), new PolicyRequest { WantsPersistence = true });

        Assert.False(decision.PersistencePossible);
        Assert.Equal(PersistenceSupport.None, decision.Persistence);
        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.PersistenceUnsupported);
    }

    [Fact]
    public void Persistence_WithoutTheRequest_DoesNotChangeTheMode()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu"));

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.False(decision.PersistencePossible);
        Assert.Equal(PersistenceSupport.ExtractMode, decision.Persistence);
        Assert.DoesNotContain(decision.Warnings, w => w.Key == ImagePolicyKeys.PersistenceUnsupported);
    }

    // ---- preferred mode ----------------------------------------------------------------------

    [Fact]
    public void PreferredMode_WhenAllowed_IsUsed()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu"), new PolicyRequest { PreferredMode = WriteMode.Extract });

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Contains(ImagePolicyKeys.PreferredModeUsed, decision.ReasonKeys);
    }

    [Fact]
    public void PreferredMode_WhenForbidden_IsRefused()
    {
        var decision = Policy.Evaluate(Hybrid("popos"), new PolicyRequest { PreferredMode = WriteMode.Extract });

        Assert.Equal(PolicyMode.RawCopy, decision.Mode);
        Assert.Contains(ImagePolicyKeys.PreferredModeRefused, decision.ReasonKeys);
    }

    // ---- label patching ----------------------------------------------------------------------

    [Theory]
    [InlineData("fedora", "Fedora-WS-Live-40-1-14", true)]
    [InlineData("fedora", "FEDORA40", false)]
    [InlineData("arch", "ARCH_202401", false)]
    [InlineData("arch", "arch_202401", true)]
    [InlineData("rhel", "Rocky-9-3-x86_64-dvd", true)]
    [InlineData("endeavouros", "EOS_202403", false)]
    [InlineData("ubuntu", "Ubuntu 24.04 LTS amd64", false)]
    [InlineData("debian-live", "d-live 12.5.0 gn amd64", false)]
    public void LabelPatch_IsRequiredWhenTheFamilyUsesTheLabelAndFatChangesIt(string family, string label, bool expected)
    {
        var decision = Policy.Evaluate(Hybrid(family, label: label), new PolicyRequest { PreferredMode = WriteMode.Extract });

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Equal(expected, decision.LabelPatchRequired);
        Assert.Equal(expected, decision.Warnings.Any(w => w.Key == ImagePolicyKeys.LabelChanged));
    }

    [Fact]
    public void LabelChangedWarning_CarriesBothLabels()
    {
        var decision = Policy.Evaluate(Hybrid("fedora", label: "Fedora-WS-Live-40-1-14"), new PolicyRequest { PreferredMode = WriteMode.Extract });

        var warning = decision.Warnings.Single(w => w.Key == ImagePolicyKeys.LabelChanged);

        Assert.Equal("Fedora-WS-Live-40-1-14", warning.Arguments[0]);
        Assert.Equal("FEDORA-WS-L", warning.Arguments[1]);
    }

    [Fact]
    public void LabelPatch_IsNeverNeededForARawCopy()
    {
        Assert.False(Policy.Evaluate(Hybrid("fedora", label: "Fedora-WS-Live-40-1-14")).LabelPatchRequired);
    }

    [Theory]
    [InlineData("ABC", "ABC")]
    [InlineData("Fedora-WS-Live-40-1-14", "FEDORA-WS-L")]
    [InlineData("a.b c", "A_B C")]
    [InlineData("x*y?z", "XYZ")]
    [InlineData("Größe", "GR__E")]
    [InlineData("tab\there", "TAB_HERE")]
    [InlineData("", "")]
    public void FatLabel_FollowsTheRufusRules(string label, string expected)
    {
        Assert.Equal(expected, FatLabel.ToValid(label));
    }

    // ---- boot support ------------------------------------------------------------------------

    [Fact]
    public void RawCopy_OfAHybridWithEsp_BootsOnBothFirmwareTypes()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu"));

        Assert.Equal(BootSupport.Yes, decision.BiosBoot);
        Assert.Equal(BootSupport.Yes, decision.UefiBoot);
    }

    [Fact]
    public void RawCopy_OfAHybridWithoutEsp_MaybeBootsUefi()
    {
        var decision = Policy.Evaluate(Hybrid("debian") with { HasEspPartition = false });

        Assert.Equal(BootSupport.Maybe, decision.UefiBoot);
        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.UefiOnlyEdk2);
    }

    [Fact]
    public void RawCopy_OfAHybridWithoutAnyEfiData_HasNoUefiBoot()
    {
        var profile = Hybrid(null) with { HasEspPartition = false, HasElToritoEfi = false, HasEfiBootFiles = false };

        Assert.Equal(BootSupport.No, Policy.Evaluate(profile).UefiBoot);
    }

    [Fact]
    public void Extract_DependsOnTheBootFilesInTheImage()
    {
        var both = Policy.Evaluate(Hybrid("ubuntu"), new PolicyRequest { PreferredMode = WriteMode.Extract });
        var biosOnly = Policy.Evaluate(Hybrid("ubuntu") with { HasEfiBootFiles = false, HasElToritoEfi = false }, new PolicyRequest { PreferredMode = WriteMode.Extract });
        var efiImageOnly = Policy.Evaluate(Hybrid("ubuntu") with { HasEfiBootFiles = false }, new PolicyRequest { PreferredMode = WriteMode.Extract });

        Assert.Equal((BootSupport.Yes, BootSupport.Yes), (both.BiosBoot, both.UefiBoot));
        Assert.Equal((BootSupport.Yes, BootSupport.No), (biosOnly.BiosBoot, biosOnly.UefiBoot));
        Assert.Equal(BootSupport.Maybe, efiImageOnly.UefiBoot);
    }

    [Theory]
    [InlineData("reactos")]
    [InlineData("kolibrios")]
    [InlineData("freedos")]
    [InlineData("windows-nt5")]
    [InlineData("winpe-xp")]
    public void LegacySystems_NeverBootUefi(string family)
    {
        var kind = family.StartsWith("win", StringComparison.Ordinal) ? ImageKind.WindowsSetup : ImageKind.OtherOs;

        Assert.Equal(BootSupport.No, Policy.Evaluate(IsoOnly(family, kind)).UefiBoot);
    }

    [Fact]
    public void RaspberryPiImage_DoesNotBootOnAPc()
    {
        var decision = Policy.Evaluate(RawDisk("raspberrypi"));

        Assert.Equal((BootSupport.No, BootSupport.No), (decision.BiosBoot, decision.UefiBoot));
        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.NotBootableOnPc);
    }

    // ---- warnings ----------------------------------------------------------------------------

    [Fact]
    public void RawCopyOfAnIso_WarnsAboutTheReadOnlyLayoutAndTheBackupGpt()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu") with { HasGpt = true });

        var layout = decision.Warnings.Single(w => w.Key == ImagePolicyKeys.DdReadOnlyLayout);
        Assert.Equal(WarningSeverity.Info, layout.Severity);
        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.GptBackupAtIsoEnd);
    }

    [Fact]
    public void ProtectiveMbrWithGpt_WarnsAboutOldBiosMachines()
    {
        var decision = Policy.Evaluate(Hybrid("arch") with { HasGpt = true, HasProtectiveMbr = true });

        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.GptProtectiveMbrLegacy);
    }

    [Fact]
    public void FourKTarget_WarnsForHybridImages()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu"), new PolicyRequest { TargetSectorSize = 4096 });

        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.Sector4kTarget);
        Assert.DoesNotContain(Policy.Evaluate(Hybrid("ubuntu")).Warnings, w => w.Key == ImagePolicyKeys.Sector4kTarget);
    }

    [Fact]
    public void ImageLargerThanTheTarget_IsAnErrorWithBothSizes()
    {
        var decision = Policy.Evaluate(Hybrid("ubuntu"), new PolicyRequest { TargetSizeBytes = 2 * GiB });

        var warning = decision.Warnings.Single(w => w.Key == ImagePolicyKeys.ImageLargerThanTarget);
        Assert.Equal(WarningSeverity.Error, warning.Severity);
        Assert.Equal(3 * GiB, warning.Arguments[0]);
        Assert.Equal(2 * GiB, warning.Arguments[1]);
        Assert.DoesNotContain(Policy.Evaluate(Hybrid("ubuntu"), new PolicyRequest { TargetSizeBytes = 4 * GiB }).Warnings, w => w.Key == ImagePolicyKeys.ImageLargerThanTarget);
    }

    [Fact]
    public void CompressedImageOfUnknownSize_WarnsBeforeARawWrite()
    {
        var profile = RawDisk("raspberrypi") with { IsCompressed = true, ImageBytes = 0 };

        Assert.Contains(Policy.Evaluate(profile).Warnings, w => w.Key == ImageWarningKeys.CompressedSizeUnknown);
    }

    [Theory]
    [InlineData(FileSystemKind.Auto, true)]
    [InlineData(FileSystemKind.Fat32, true)]
    [InlineData(FileSystemKind.Ntfs, false)]
    [InlineData(FileSystemKind.ExFat, false)]
    public void FilesOver4GiB_WarnForFatTargetsInExtractMode(FileSystemKind target, bool expected)
    {
        var profile = Hybrid("arch") with { LargestFileBytes = 5 * GiB };

        var decision = Policy.Evaluate(profile, new PolicyRequest { PreferredMode = WriteMode.Extract, TargetFileSystem = target });

        Assert.Equal(expected, decision.Warnings.Any(w => w.Key == ImagePolicyKeys.FatFileLimit));
    }

    [Fact]
    public void WindowsMedia_NeverWarnAboutTheFatLimitBecauseTheInstallImageIsSplit()
    {
        var profile = IsoOnly("windows", ImageKind.WindowsSetup) with { LargestFileBytes = 6 * GiB };

        Assert.DoesNotContain(Policy.Evaluate(profile).Warnings, w => w.Key == ImagePolicyKeys.FatFileLimit);
    }

    [Theory]
    [InlineData(FileSystemKind.Ntfs, true)]
    [InlineData(FileSystemKind.ExFat, true)]
    [InlineData(FileSystemKind.Fat32, false)]
    public void NtfsAndExFat_NeedTheUefiHelper(FileSystemKind target, bool expected)
    {
        var decision = Policy.Evaluate(Hybrid("arch"), new PolicyRequest { PreferredMode = WriteMode.Extract, TargetFileSystem = target });

        Assert.Equal(expected, decision.Warnings.Any(w => w.Key == ImagePolicyKeys.NtfsExfatHelper));
    }

    [Fact]
    public void CasperOnExFat_IsReportedAsUnworkable()
    {
        var decision = Policy.Evaluate(Hybrid("linuxmint"), new PolicyRequest { PreferredMode = WriteMode.Extract, TargetFileSystem = FileSystemKind.ExFat });

        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.CasperExfat);
        Assert.DoesNotContain(Policy.Evaluate(Hybrid("arch"), new PolicyRequest { PreferredMode = WriteMode.Extract, TargetFileSystem = FileSystemKind.ExFat }).Warnings, w => w.Key == ImagePolicyKeys.CasperExfat);
    }

    [Fact]
    public void LinuxExtraction_MentionsTheBootloaderVersionMatch()
    {
        var decision = Policy.Evaluate(Hybrid("fedora"), new PolicyRequest { PreferredMode = WriteMode.Extract });

        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.BootloaderOffline);
    }

    [Fact]
    public void EmulatedBootImage_IsLegacyOnly()
    {
        var profile = IsoOnly("freedos", ImageKind.Dos) with { HasEmulatedBootImage = true };

        Assert.Contains(Policy.Evaluate(profile).Warnings, w => w.Key == ImagePolicyKeys.LegacyBiosOnly);
    }

    [Fact]
    public void BsdIso_IsMeantForOpticalMedia()
    {
        var profile = IsoOnly("freebsd", ImageKind.Bsd);

        var decision = Policy.Evaluate(profile);

        Assert.Contains(decision.Warnings, w => w.Key == ImagePolicyKeys.BsdIsoForOptical);
        Assert.Equal(PolicyMode.Extract, decision.Mode);
    }

    [Fact]
    public void FamilyWarnings_AreAttachedToTheirFamily()
    {
        Assert.Contains(Policy.Evaluate(IsoOnly("hirens-pe", ImageKind.WindowsPe)).Warnings, w => w.Key == ImagePolicyKeys.RevokedLoaders);
        Assert.Contains(Policy.Evaluate(IsoOnly("esxi", ImageKind.OtherOs)).Warnings, w => w.Key == ImagePolicyKeys.EsxiSyslinux);
        Assert.DoesNotContain(Policy.Evaluate(IsoOnly("winpe", ImageKind.WindowsPe)).Warnings, w => w.Key == ImagePolicyKeys.RevokedLoaders);
    }

    [Fact]
    public void WarningsAreNotRepeated()
    {
        var decision = Policy.Evaluate(Hybrid("kali"), new PolicyRequest { WantsPersistence = true, TargetSectorSize = 4096 });

        Assert.Equal(decision.Warnings.Count, decision.Warnings.Select(w => w.Key).Distinct().Count());
    }

    // ---- the data itself ---------------------------------------------------------------------

    [Fact]
    public void Inheritance_AddsListsAndOverridesValues()
    {
        var json = """
            { "families": {
                "base": { "modes": ["RawCopy","Extract"], "default": "RawCopy", "labelPatch": true, "reasons": ["a"], "warnings": ["w1"], "flags": ["f1"], "raw": { "bios": "Yes", "uefi": "Maybe" } },
                "child": { "inherits": "base", "default": "Extract", "reasons": ["b"], "flags": ["f2"], "raw": { "uefi": "No" } }
            } }
            """;

        var policy = ImagePolicy.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var profile = Hybrid("child") with { HasBiosBootFiles = false, HasEspPartition = false, HasElToritoBios = false, HasElToritoEfi = false, HasEfiBootFiles = false };

        var decision = policy.Evaluate(profile);

        Assert.Equal(PolicyMode.Extract, decision.Mode);
        Assert.Contains("a", decision.ReasonKeys);
        Assert.Contains(decision.Warnings, w => w.Key == "w1");
        var raw = policy.Evaluate(profile, new PolicyRequest { PreferredMode = WriteMode.RawCopy });
        Assert.Equal((BootSupport.Yes, BootSupport.No), (raw.BiosBoot, raw.UefiBoot));
    }

    [Fact]
    public void Load_InheritanceCycle_IsRejected()
    {
        var json = """{ "families": { "a": { "inherits": "b" }, "b": { "inherits": "a" } } }""";

        Assert.Throws<InvalidDataException>(() => ImagePolicy.Load(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void Load_UnknownParent_IsRejected()
    {
        var json = """{ "families": { "a": { "inherits": "missing" } } }""";

        Assert.Throws<InvalidDataException>(() => ImagePolicy.Load(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void FamilyLookup_IsCaseInsensitive()
    {
        Assert.True(Policy.Knows("UBUNTU"));
        Assert.Equal(PolicyMode.RawCopy, Policy.Evaluate(Hybrid("Ubuntu")).Mode);
        Assert.False(Policy.Knows("not-a-family"));
    }

    [Fact]
    public void EveryFingerprintFamily_HasAPolicyEntry()
    {
        var families = FamilyRuleFamilies();

        Assert.NotEmpty(families);
        Assert.All(families, family => Assert.True(Policy.Knows(family), $"policy has no entry for '{family}'"));
    }

    private static List<string> FamilyRuleFamilies()
    {
        using var stream = typeof(ImageProfile).Assembly.GetManifestResourceStream("Bootrix.Core.Images.Families.family-rules.json")!;
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("rules").EnumerateArray().Select(rule => rule.GetProperty("family").GetString()!).Distinct().ToList();
    }
}
