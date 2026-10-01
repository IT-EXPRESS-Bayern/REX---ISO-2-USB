// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Core.Tests.Images;

public sealed class ImageFileTypesTests
{
    [Theory]
    [InlineData("ubuntu.iso", true)]
    [InlineData("UBUNTU.ISO", true)]
    [InlineData("disk.img.xz", true)]
    [InlineData("install.wim", true)]
    [InlineData("install.ESD", true)]
    [InlineData("install2.swm", true)]
    [InlineData("backup.vhdx", true)]
    [InlineData("disk.Z", true)]
    [InlineData("archive.zip", true)]
    [InlineData("notes.txt", false)]
    [InlineData("noextension", false)]
    [InlineData("tarball.tar", false)]
    public void IsKnown_FollowsTheExtension(string path, bool expected) => Assert.Equal(expected, ImageFileTypes.IsKnown(path));

    [Theory]
    [InlineData("a.gz", true)]
    [InlineData("a.bz2", true)]
    [InlineData("a.zst", true)]
    [InlineData("a.lzma", true)]
    [InlineData("a.XZ", true)]
    [InlineData("a.iso", false)]
    [InlineData("a.wim", false)]
    public void IsCompressed_CoversTheSupportedContainers(string path, bool expected) =>
        Assert.Equal(expected, ImageFileTypes.IsCompressed(path));

    [Theory]
    [InlineData("disk.img.xz", ".img")]
    [InlineData("/downloads/ubuntu.iso", ".iso")]
    [InlineData("ubuntu.iso.zst", ".iso")]
    [InlineData("backup.tar.gz", ".tar")]
    [InlineData("image.xz", "")]
    [InlineData("plain", "")]
    public void GetImageExtension_LooksThroughTheCompression(string path, string expected) =>
        Assert.Equal(expected, ImageFileTypes.GetImageExtension(path));

    [Fact]
    public void FilterPattern_ListsEveryExtensionAsAGlob()
    {
        var patterns = ImageFileTypes.FilterPattern.Split(';');

        Assert.All(patterns, pattern => Assert.StartsWith("*.", pattern, StringComparison.Ordinal));
        Assert.Contains("*.iso", patterns);
        Assert.Contains("*.wim", patterns);
        Assert.Contains("*.xz", patterns);
        Assert.Equal(
            ImageFileTypes.DiskImageExtensions.Count + ImageFileTypes.WindowsImageExtensions.Count + ImageFileTypes.CompressedExtensions.Count,
            patterns.Length);
        Assert.Equal(patterns.Length, patterns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
