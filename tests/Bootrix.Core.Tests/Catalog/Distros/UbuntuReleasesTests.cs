// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class UbuntuReleasesTests
{
    [Fact]
    public void MetaRelease_ReadsVersionLtsFlagSupportAndReleaseDate()
    {
        var releases = UbuntuReleases.ParseMetaRelease(DistroFixtures.Text("ubuntu/meta-release"));

        var resolute = Assert.Single(releases, r => r.Dist == "resolute");
        Assert.Equal("26.04", resolute.Series);
        Assert.Equal("26.04.1", resolute.Version.ToString());
        Assert.True(resolute.IsLts);
        Assert.True(resolute.Supported);
        Assert.Equal(new DateOnly(2026, 4, 23), resolute.Date);

        var questing = Assert.Single(releases, r => r.Dist == "questing");
        Assert.Equal("25.10", questing.Series);
        Assert.False(questing.IsLts);
        Assert.False(questing.Supported);
        Assert.Equal(new DateOnly(2025, 10, 9), questing.Date);
    }

    [Fact]
    public void MetaRelease_KeepsTheTwoDigitMinorOfTheDirectoryName()
    {
        var releases = UbuntuReleases.ParseMetaRelease(DistroFixtures.Text("ubuntu/meta-release"));

        Assert.Equal("22.04", Assert.Single(releases, r => r.Dist == "jammy").Series);
        Assert.Equal("24.10", Assert.Single(releases, r => r.Dist == "oracular").Series);
    }

    [Fact]
    public void MetaRelease_IgnoresBlocksWithoutDistOrVersion()
    {
        var releases = UbuntuReleases.ParseMetaRelease("Name: orphan\n\nDist: x\nName: no version\n\nDist: ok\nVersion: 24.04 LTS\nSupported: 1\n");

        var only = Assert.Single(releases);
        Assert.Equal("ok", only.Dist);
        Assert.Null(only.Date);
    }

    [Fact]
    public void LatestImages_PicksTheHighestPointReleasePerKind()
    {
        var sums = ChecksumFile.Parse(DistroFixtures.Text("ubuntu/ubuntu-24.04.SHA256SUMS"));

        var images = UbuntuReleases.LatestImages(sums, "ubuntu");

        // 24.04.5.1 is a respin of the desktop image and sorts above 24.04.5; the server stays at 24.04.5.
        Assert.Collection(
            images,
            desktop =>
            {
                Assert.Equal("ubuntu-24.04.5.1-desktop-amd64.iso", desktop.FileName);
                Assert.Equal("desktop", desktop.Kind);
            },
            server =>
            {
                Assert.Equal("ubuntu-24.04.5-live-server-amd64.iso", server.FileName);
                Assert.Equal("server", server.Kind);
            });
    }

    [Fact]
    public void LatestImages_IgnoresWslAndOtherFlavoursInTheSameFile()
    {
        var sums = ChecksumFile.Parse(
            new string('0', 64) + " *ubuntu-24.04.5-wsl-amd64.wsl\n"
            + new string('1', 64) + " *ubuntu-mate-24.04.5-desktop-amd64.iso\n"
            + new string('2', 64) + " *ubuntu-24.04.5-desktop-arm64.iso\n");

        Assert.Empty(UbuntuReleases.LatestImages(sums, "ubuntu"));
        Assert.Single(UbuntuReleases.LatestImages(sums, "ubuntu-mate"));
    }
}
