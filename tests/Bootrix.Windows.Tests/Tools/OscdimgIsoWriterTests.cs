// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Windows.Tools;

namespace Bootrix.Windows.Tests.Tools;

public sealed class OscdimgIsoWriterTests : IDisposable
{
    private readonly string _media = Path.Combine(Path.GetTempPath(), "bootrix-iso-" + Guid.NewGuid().ToString("N"));

    public OscdimgIsoWriterTests()
    {
        Directory.CreateDirectory(Path.Combine(_media, "boot"));
        Directory.CreateDirectory(Path.Combine(_media, "efi", "microsoft", "boot"));
        File.WriteAllText(Path.Combine(_media, "boot", "etfsboot.com"), "b");
    }

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private void Efi(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_media, "efi", "microsoft", "boot", name), "e");
        }
    }

    [Theory]
    [InlineData("  5% complete", 5)]
    [InlineData("100% complete", 100)]
    [InlineData("Progress: 42% Complete", 42)]
    public void ParsesProgressLines(string line, double expected)
    {
        Assert.True(OscdimgIsoWriter.TryParsePercent(line, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("Scanning source tree")]
    [InlineData("")]
    [InlineData("100%")]
    public void IgnoresOtherOutput(string line)
    {
        Assert.False(OscdimgIsoWriter.TryParsePercent(line, out _));
    }

    [Theory]
    [InlineData("Windows 11 Tiny", "WINDOWS_11_TINY")]
    [InlineData("ÄÖÜ-Test!", "____TEST_")]
    [InlineData("", "BOOTRIX")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    public void LabelsAreReducedToTheSafeSubset(string input, string expected)
    {
        Assert.Equal(expected, OscdimgIsoWriter.SanitizeLabel(input));
    }

    [Fact]
    public void ArgumentsDescribeBothBootEntries()
    {
        Efi("efisys.bin", "efisys_noprompt.bin");

        var arguments = OscdimgIsoWriter.BuildArguments(_media, "out.iso", "TINY11", uefi2023: false);

        Assert.StartsWith("-m -o -u2 -udfver102 -lTINY11 ", arguments, StringComparison.Ordinal);
        Assert.Contains("-bootdata:2#p0,e,b\"", arguments, StringComparison.Ordinal);
        Assert.Contains("#pEF,e,b\"", arguments, StringComparison.Ordinal);
        Assert.Contains("efisys_noprompt.bin", arguments, StringComparison.Ordinal);
        Assert.EndsWith($"\"{_media}\" \"out.iso\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptingImageIsUsedWhenTheQuietOneIsMissing()
    {
        Efi("efisys.bin");

        Assert.EndsWith("efisys.bin", OscdimgIsoWriter.FindEfiBootImage(_media, uefi2023: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Uefi2023NeedsTheExImage()
    {
        Efi("efisys.bin");

        var ex = Assert.Throws<BootrixException>(() => OscdimgIsoWriter.FindEfiBootImage(_media, uefi2023: true));
        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);

        Efi("efisys_EX.bin");
        Assert.EndsWith("efisys_EX.bin", OscdimgIsoWriter.FindEfiBootImage(_media, uefi2023: true), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBiosLoaderIsReported()
    {
        Efi("efisys.bin");
        File.Delete(Path.Combine(_media, "boot", "etfsboot.com"));

        Assert.Throws<BootrixException>(() => OscdimgIsoWriter.BuildArguments(_media, "out.iso", "X", false));
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "amd64")]
    public void AdkFolderFollowsTheArchitecture(System.Runtime.InteropServices.Architecture architecture, string expected)
    {
        _ = architecture;
        Assert.Contains(OscdimgLocator.AdkArchitectureFolder(), new[] { expected, "arm64", "x86" });
    }
}
