// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Images;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images;

public sealed class ImageInspectorFamilyTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> CaseNames => [.. IsoFamilyCases.Names];

    /// <summary>A hybrid ISO with BIOS and EFI El Torito entries, as most Linux media are built.</summary>
    internal string BuildHybrid(string name, IReadOnlyDictionary<string, string> files, string label)
    {
        var efi = IsoBuilder.FatImage(_dir, "efi-" + name + ".img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "efi" });
        var tree = files.ToDictionary(file => file.Key, file => Encoding.UTF8.GetBytes(file.Value), StringComparer.Ordinal);
        tree["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader();
        tree["boot/efi.img"] = efi;
        tree["EFI/BOOT/BOOTX64.EFI"] = "efi"u8.ToArray();

        return IsoBuilder.Build(_dir, name, tree, new IsoOptions
        {
            Label = label.Length > 32 ? label[..32] : label,
            Hybrid = true,
            Gpt = true,
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });
    }

    [ToolTheory("xorriso", "mkfs.vfat", "mcopy")]
    [MemberData(nameof(CaseNames))]
    public async Task Inspect_FamilyMarkers_ClassifyTheImage(string name)
    {
        var expected = IsoFamilyCases.Get(name);
        var iso = BuildHybrid(name, expected.Files, expected.Label);

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(expected.Family, result.Profile.Family);
        Assert.Equal(expected.Kind, result.Profile.Kind);
        if (expected.Release is not null)
        {
            Assert.Equal(expected.Release, result.ReleaseInfo);
        }

        Assert.True(result.Profile.IsHybrid);
        Assert.True(result.Profile.HasElToritoBios);
        Assert.True(result.Profile.HasElToritoEfi);
        Assert.True(result.Profile.HasBiosBootFiles);
        Assert.True(result.Profile.HasEfiBootFiles);
        Assert.True(result.Profile.HasEspPartition);
        Assert.Equal(ImageContainer.Iso9660, result.Container);
        Assert.False(result.IsTruncated);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task Inspect_UnknownBootableIso_GetsNoFamilyButAKind()
    {
        var iso = BuildHybrid("unknown", new Dictionary<string, string> { ["stuff/readme.txt"] = "nothing special" }, "STUFF");

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Null(result.Profile.Family);
        Assert.Equal(ImageKind.LinuxHybrid, result.Profile.Kind);
    }

    [ToolFact("xorriso")]
    public async Task Inspect_NonHybridIsolinuxIso_IsIsoOnly()
    {
        var tree = new Dictionary<string, byte[]>
        {
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
            ["isolinux/isolinux.cfg"] = "default x"u8.ToArray(),
            ["files/a.txt"] = "a"u8.ToArray(),
        };
        var iso = IsoBuilder.Build(_dir, "plain", tree, new IsoOptions { BootImages = [new BootImageSpec("isolinux/isolinux.bin")] });

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.False(result.Profile.IsHybrid);
        Assert.Equal(ImageKind.LinuxIsoOnly, result.Profile.Kind);
        Assert.True(result.Profile.HasBiosBootFiles);
        Assert.False(result.Profile.HasEfiBootFiles);
        Assert.False(result.Profile.HasElToritoEfi);
        Assert.False(result.Profile.HasEspPartition);
    }

    [ToolFact("xorriso")]
    public async Task Inspect_DataIsoWithoutBootData_IsDataAndNotBootable()
    {
        var tree = new Dictionary<string, byte[]> { ["docs/a.txt"] = "a"u8.ToArray(), ["b.bin"] = new byte[5000] };
        var iso = IsoBuilder.Build(_dir, "data", tree);

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(ImageKind.Data, result.Profile.Kind);
        Assert.False(result.Profile.IsHybrid);
        Assert.Null(result.BootCatalog);
        Assert.Contains(result.Warnings, w => w.Key == ImageWarningKeys.NotBootable);
        Assert.Equal(5001, result.Profile.TotalBytes);
        Assert.Equal(5000, result.Profile.LargestFileBytes);
        Assert.Equal(2, result.FileCount);
    }
}
