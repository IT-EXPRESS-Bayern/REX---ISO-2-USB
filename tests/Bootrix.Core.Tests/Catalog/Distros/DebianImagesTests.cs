// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class DebianImagesTests
{
    [Fact]
    public void Netinst_SkipsTheEduAndMacInstallersOfTheSameDirectory()
    {
        var images = DebianImages.Netinst(ChecksumFile.Parse(DistroFixtures.Text("debian/netinst.SHA256SUMS")));

        var image = Assert.Single(images);
        Assert.Equal("debian-13.7.0-amd64-netinst.iso", image.FileName);
        Assert.Equal("13.7.0", image.Version);
        Assert.Equal("amd64", image.Architecture);
        Assert.Equal("netinst", image.Edition);
    }

    [Fact]
    public void Live_ListsOneImagePerDesktopAndNotTheFilesAroundThem()
    {
        var images = DebianImages.Live(ChecksumFile.Parse(DistroFixtures.Text("debian/live.SHA256SUMS")));

        // The checksum file also covers .contents, .log and .packages for every image.
        Assert.Equal(
            ["cinnamon", "debian-junior", "gnome", "kde", "lxde", "lxqt", "mate", "standard", "xfce"],
            images.Select(i => i.Edition).Order());
        Assert.All(images, i => Assert.EndsWith(".iso", i.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void Netinst_FindsTheArm64InstallerInItsOwnDirectory()
    {
        var images = DebianImages.Netinst(ChecksumFile.Parse(DistroFixtures.Text("debian/netinst-arm64.SHA256SUMS")));

        Assert.Equal("arm64", Assert.Single(images).Architecture);
    }
}
