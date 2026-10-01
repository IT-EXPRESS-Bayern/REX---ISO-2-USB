// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Localization;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Images.Udif;
using Bootrix.Core.Writing;
using Bootrix.Core.Writing.Raw;
using static Bootrix.Core.Tests.Images.Apple.AppleTestImages;

namespace Bootrix.Core.Tests.Writing.Raw;

/// <summary>Mac images as sources of a raw write: the volume inside the container is what is planned, and the boot limits come as warnings.</summary>
public sealed class AppleImagePlanningTests : IDisposable
{
    private const int Mib = 1024 * 1024;

    private readonly TestDirectory _dir = new("bootrix-apple");
    private readonly MediaPlanService _service = new(new ImageInspector());

    public void Dispose() => _dir.Dispose();

    private static StorageDevice Stick(long size) => new()
    {
        DiskNumber = 3,
        DevicePath = @"\\?\stick",
        Bus = BusType.Usb,
        IsRemovableMedia = true,
        SizeBytes = size,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 512,
    };

    /// <summary>An Apple partition map with a blessed HFS+ volume: the shape of an installer disk of the PowerPC and early Intel era.</summary>
    private static byte[] ApmMacDisk(int size = 8 * Mib)
    {
        var image = new byte[size];
        WriteApm(
            image,
            512,
            ("Apple", "Apple_partition_map", 1, 63),
            ("Macintosh HD", "Apple_HFS", 64, (uint)((size / 512) - 64)));
        WriteHfsPlusHeader(image.AsSpan(64 * 512), blessedFolder: 7);
        return image;
    }

    private string Dmg(string name, byte[] volume)
    {
        var sectors = volume.Length / 512;
        var dmg = UdifBuilder.FromVolume(volume, [($"disk image (Apple_HFS : 0)", 0, sectors)], 2048, (index, data) => DmgFixtures.Encode("zlib", index, data));
        return _dir.Write(name, dmg.Build());
    }

    [Fact]
    public async Task DmgWithAMacDisk_IsPlannedAsRawCopyWithTheMacBootNotes()
    {
        var volume = ApmMacDisk();
        var path = Dmg("Install.dmg", volume);

        var preview = await _service.PlanAsync(path, new TargetOptions(), Stick(64L * Mib));

        Assert.Equal(WriteMethod.RawCopy, preview.Plan.WriteMethod);
        Assert.Equal(ImageKind.Apple, preview.Inspection.Profile.Kind);
        Assert.Equal(volume.Length, preview.Inspection.Profile.TotalBytes);
        var codes = preview.Plan.Warnings.Select(w => w.Code).ToList();
        Assert.Contains("Apple.Hint.BootableMacVolume", codes);
        Assert.Contains("Apple.Hint.T2Restriction", codes);
        Assert.Contains("Apple.Hint.AppleSiliconUnsupported", codes);
    }

    [Fact]
    public async Task DmgWithABareHfsPlusVolume_IsCopiedRawInsteadOfExtracted()
    {
        var volume = new byte[8 * Mib];
        WriteHfsPlusHeader(volume, blessedFolder: 2);
        var path = Dmg("Volume.dmg", volume);

        var preview = await _service.PlanAsync(path, new TargetOptions(), Stick(64L * Mib));

        Assert.Equal(WriteMethod.RawCopy, preview.Plan.WriteMethod);
        Assert.Equal(ImageKind.Apple, preview.Inspection.Profile.Kind);
        Assert.Contains(preview.Plan.Warnings, w => w.Code == "Apple.Hint.BareVolume");
    }

    [Fact]
    public async Task VolumeLargerThanTheStick_IsTooLargeEvenThoughTheFileIsSmall()
    {
        var volume = ApmMacDisk(64 * Mib);
        var path = Dmg("Big.dmg", volume);
        Assert.True(new FileInfo(path).Length < 4 * Mib);

        var error = await Assert.ThrowsAsync<BootrixException>(() => _service.PlanAsync(path, new TargetOptions(), Stick(32L * Mib)));

        Assert.Equal(ErrorCode.DeviceTooSmall, error.Code);
    }

    [Fact]
    public async Task UnpackedMacImage_WithAnApmFile_GetsTheSameTreatment()
    {
        var path = _dir.Write("disc.cdr", ApmMacDisk());

        var preview = await _service.PlanAsync(path, new TargetOptions(), Stick(64L * Mib));

        Assert.Equal(WriteMethod.RawCopy, preview.Plan.WriteMethod);
        Assert.Contains(preview.Plan.Warnings, w => w.Code == "Apple.Hint.T2Restriction");
    }

    [Fact]
    public async Task ImageWithoutAnythingFromApple_GetsNoNotes()
    {
        var disk = new byte[8 * Mib];
        MbrSignature(disk);
        var path = _dir.Write("pc.img", disk);

        var preview = await _service.PlanAsync(path, new TargetOptions(), Stick(64L * Mib));

        Assert.DoesNotContain(preview.Plan.Warnings, w => w.Code.StartsWith("Apple.Hint.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EncryptedDmg_IsRefusedWhileLookingAtIt()
    {
        var content = new byte[8192];
        "encrcdsa"u8.CopyTo(content);
        var path = _dir.Write("secret.dmg", content);

        var error = await Assert.ThrowsAsync<BootrixException>(() => _service.PlanAsync(path, new TargetOptions(), Stick(64L * Mib)));

        Assert.Equal(ErrorCode.ImageEncrypted, error.Code);
    }

    [Fact]
    public void Apply_DoesNotDuplicateAWarningThePlanHasAlready()
    {
        var inspection = new ImageInspection { Profile = new ImageProfile(), Container = ImageContainer.Unknown };
        var refinement = new AppleRefinement(inspection, [new PlanWarning("Apple.Hint.T2Restriction")]);
        var plan = new MediaPlan { Warnings = [new PlanWarning("Apple.Hint.T2Restriction")] };

        Assert.Single(refinement.Apply(plan).Warnings);
    }

    [Fact]
    public void EveryHint_HasAGermanAndAnEnglishText()
    {
        var german = new Localizer { Culture = new System.Globalization.CultureInfo("de") };
        var english = new Localizer { Culture = new System.Globalization.CultureInfo("en") };

        Assert.All(Enum.GetValues<AppleImageHint>(), hint =>
        {
            Assert.True(german.Has(hint.ResourceKey()), hint.ToString());
            Assert.NotEqual(german.Get(hint.ResourceKey()), english.Get(hint.ResourceKey()));
        });
    }

    private static void MbrSignature(byte[] disk)
    {
        disk[0] = 0xEB;
        disk[446 + 4] = 0x83;
        disk[446 + 8] = 1;
        disk[446 + 12] = 100;
        disk[510] = 0x55;
        disk[511] = 0xAA;
    }
}
