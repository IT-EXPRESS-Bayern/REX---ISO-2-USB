// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class FedoraReleasesTests
{
    private static IReadOnlyList<FedoraImage> Images() => FedoraReleases.Parse(DistroFixtures.Text("fedora/releases.json"));

    [Fact]
    public void OnlyInstallableIsosOfFinalReleasesAreKept()
    {
        var images = Images();

        Assert.DoesNotContain(images, i => i.Release == 45);
        Assert.All(images, i => Assert.EndsWith(".iso", i.FileName, StringComparison.Ordinal));
        Assert.DoesNotContain(images, i => i.Variant is "Cloud" or "Container");
        Assert.DoesNotContain(images, i => i.VendorArchitecture is "ppc64le" or "s390x");
    }

    [Fact]
    public void LiveImage_CarriesReleaseComposeArchitectureAndSize()
    {
        var workstation = Assert.Single(Images(), i => i is { Release: 44, Variant: "Workstation", Architecture: "x64" });

        Assert.Equal("Fedora-Workstation-Live-44-1.7.x86_64.iso", workstation.FileName);
        Assert.Equal("1.7", workstation.Compose);
        Assert.Equal("live", workstation.Kind);
        Assert.Equal("x86_64", workstation.VendorArchitecture);
        Assert.Equal(2_851_612_672, workstation.Size);
        Assert.Equal(
            new Uri("https://download.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso"),
            workstation.Link);
    }

    [Fact]
    public void ServerInstallers_AreToldApartByTheirKind()
    {
        var server = Images().Where(i => i is { Release: 44, Variant: "Server", Architecture: "x64" }).ToList();

        Assert.Equal(["dvd", "netinst"], server.Select(i => i.Kind).Order());
        Assert.All(server, i => Assert.Equal("1.7", i.Compose));
        Assert.Contains(server, i => i.FileName == "Fedora-Server-netinst-x86_64-44-1.7.iso");
    }

    [Fact]
    public void Arm64_IsOfferedBesideX64()
    {
        var kde = Images().Where(i => i is { Release: 44, Variant: "KDE" }).ToList();

        // The aarch64 raw disk image of the same edition is not an ISO and is skipped.
        Assert.Equal(["arm64", "x64"], kde.Select(i => i.Architecture).Order());
    }

    [Fact]
    public void Spins_AreListedBySubVariant()
    {
        var spins = Images().Where(i => i is { Release: 44, Variant: "Spins", Architecture: "x64" }).Select(i => i.SubVariant).Order().ToList();

        Assert.Equal(["Cinnamon", "KDE_Mobile", "Mate", "SoaS", "Xfce"], spins);
    }

    [Fact]
    public void WrongShapeOfJson_YieldsNothing_AndBrokenJsonIsAnError()
    {
        Assert.Empty(FedoraReleases.Parse("{}"));
        Assert.Empty(FedoraReleases.Parse("[1, \"x\", {\"version\": 44}]"));

        var error = Assert.Throws<BootrixException>(() => FedoraReleases.Parse("<html>maintenance</html>"));
        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public void SizeGivenAsANumber_IsAcceptedToo()
    {
        const string json = """[{"version":"44","arch":"x86_64","link":"https://d.example/Fedora-Server-dvd-x86_64-44-1.7.iso","variant":"Server","subvariant":"Server","size":123}]""";

        Assert.Equal(123, Assert.Single(FedoraReleases.Parse(json)).Size);
    }
}
