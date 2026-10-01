// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class DirectoryListingTests
{
    [Fact]
    public void Apache_ListsFilesAndDirectoriesWithoutSortLinksOrParent()
    {
        var entries = DirectoryListing.Parse(DistroFixtures.Text("debian/netinst-index.html"));

        Assert.Equal(
            ["SHA256SUMS", "SHA256SUMS.sign", "SHA512SUMS", "SHA512SUMS.sign", "debian-13.7.0-amd64-netinst.iso", "debian-edu-13.7.0-amd64-netinst.iso", "debian-mac-13.7.0-amd64-netinst.iso"],
            entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.False(e.IsDirectory));
    }

    [Fact]
    public void Lighttpd_ReadsExactSizesInBytes()
    {
        var entries = DirectoryListing.Parse(DistroFixtures.Text("freebsd/iso-images-15.1.html"));

        var memstick = Assert.Single(entries, e => e.Name == "FreeBSD-15.1-RELEASE-amd64-memstick.img");
        Assert.Equal(1_552_601_600, memstick.Size);
    }

    [Fact]
    public void HumanReadableSizes_AreNotTreatedAsExact()
    {
        var entries = DirectoryListing.Parse(DistroFixtures.Text("kali/current-index.html"));

        var iso = Assert.Single(entries, e => e.Name == "kali-linux-2026.2-installer-amd64.iso");
        Assert.Null(iso.Size);
    }

    [Fact]
    public void Nginx_RecognisesDirectoriesByTheirTrailingSlash()
    {
        var entries = DirectoryListing.Parse(DistroFixtures.Text("mint/stable-index.html"));

        Assert.Contains(entries, e => e is { Name: "22.3", IsDirectory: true });
        Assert.DoesNotContain(entries, e => e.Name == "..");
    }

    [Fact]
    public void AbsoluteAndForeignLinks_AreNotEntries()
    {
        const string html = """
            <a href="?C=N;O=D">Name</a> <a href="/parent/">up</a> <a href="../">..</a> <a href="https://elsewhere.example/x.iso">x</a>
            <a href="mailto:a@b.example">mail</a> <a href="./kept.iso">kept</a> <a href="a%20b.iso">escaped</a> <a href="kept.iso">again</a>
            """;

        Assert.Equal(["kept.iso", "a b.iso"], DirectoryListing.Parse(html).Select(e => e.Name));
    }

    [Fact]
    public void EmptyPage_HasNoEntries()
    {
        Assert.Empty(DirectoryListing.Parse(string.Empty));
    }
}
