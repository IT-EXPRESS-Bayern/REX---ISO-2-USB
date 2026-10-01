// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class PopOsProviderTests
{
    private const string Api = "https://api.pop-os.org/builds/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    /// <summary>The API answers 404 for 26.04, which System76 has not released.</summary>
    private static FakeWeb PopSite() => new FakeWeb()
        .ServeFixture(Api + "24.04/intel", "popos/24.04-intel.json")
        .ServeFixture(Api + "24.04/nvidia", "popos/24.04-nvidia.json")
        .ServeFixture(Api + "22.04/intel", "popos/22.04-intel.json")
        .ServeFixture(Api + "22.04/nvidia", "popos/22.04-nvidia.json");

    private PopOsProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Build_ReadsAddressSizeAndDigest()
    {
        var build = PopOsBuild.Parse(DistroFixtures.Text("popos/24.04-intel.json"));

        Assert.NotNull(build);
        Assert.Equal("24.04", build.Version);
        Assert.Equal("intel", build.Channel);
        Assert.Equal(new Uri("https://iso.pop-os.org/24.04/amd64/intel/20/pop-os_24.04_amd64_intel_20.iso"), build.Url);
        Assert.Equal(2_955_067_392, build.Size);
        Assert.Equal("a0ef3842ab710db4f4407cf3499560b59dddbbcd59bee17beab7b0e99dc22b4c", build.Sha256.Hex);
        Assert.Equal("20", build.Build);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"version":"24.04","channel":"intel","url":"http://iso.pop-os.org/x.iso","size":1,"sha_sum":"%H%"}""")]
    [InlineData("""{"version":"24.04","channel":"intel","url":"https://iso.pop-os.org/x.iso","size":0,"sha_sum":"%H%"}""")]
    [InlineData("""{"version":"24.04","channel":"intel","url":"https://iso.pop-os.org/x.iso","sha_sum":"%H%"}""")]
    [InlineData("""{"version":"24.04","channel":"intel","url":"https://iso.pop-os.org/x.iso","size":1,"sha_sum":"xyz"}""")]
    public void Build_WithoutAUsableFieldIsNotAnAnswer(string json)
    {
        Assert.Null(PopOsBuild.Parse(json.Replace("%H%", new string('a', 64), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Variants_ArePerReleaseAndGraphicsChannel_ReleasesTheApiDoesNotKnowAreSkipped()
    {
        var variants = await Provider(PopSite()).ListVariantsAsync("popos", CancellationToken.None);

        Assert.Equal(["24.04/intel", "24.04/nvidia", "22.04/intel", "22.04/nvidia"], variants.Select(v => v.Id));
        Assert.Equal(
            ["Pop!_OS 24.04 LTS (Intel/AMD graphics)", "Pop!_OS 24.04 LTS (NVIDIA graphics)", "Pop!_OS 22.04 LTS (Intel/AMD graphics)", "Pop!_OS 22.04 LTS (NVIDIA graphics)"],
            variants.Select(v => v.Name));
        Assert.Equal([2_955_067_392, 3_934_945_280, 2_751_463_424, 3_242_065_920], variants.Select(v => v.SizeBytes));
        Assert.All(variants, v => Assert.Equal(["x64"], v.Architectures));
    }

    [Fact]
    public async Task OnlyTheNewestReleasesPlainImageIsRecommended()
    {
        var variants = await Provider(PopSite()).ListVariantsAsync("popos", CancellationToken.None);

        Assert.Equal(["24.04/intel"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Variants_AreOnlyTlsProtected()
    {
        var variants = await Provider(PopSite()).ListVariantsAsync("popos", CancellationToken.None);

        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Resolve_UsesTheAddressDigestAndSizeOfTheBuild()
    {
        var provider = Provider(PopSite());
        var variant = (await provider.ListVariantsAsync("popos", CancellationToken.None)).First(v => v.Id == "24.04/nvidia");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri("https://iso.pop-os.org/24.04/amd64/nvidia/28/pop-os_24.04_amd64_nvidia_28.iso"), request.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "bd867a6b93810769b2189471cfc2d7b6b9c642213c7bf827de511be5f42ce666"), Assert.Single(request.ExpectedHashes));
        Assert.Equal(3_934_945_280, request.ExpectedSize);
    }

    [Fact]
    public async Task Resolve_FollowsANewBuildOfTheSameRelease()
    {
        var web = PopSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("popos", CancellationToken.None)).First(v => v.Id == "24.04/intel");
        web.Serve(Api + "24.04/intel", DistroFixtures.Text("popos/24.04-intel.json").Replace("intel/20/pop-os_24.04_amd64_intel_20", "intel/21/pop-os_24.04_amd64_intel_21", StringComparison.Ordinal));
        _time.Advance(TimeSpan.FromMinutes(11));

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.EndsWith("pop-os_24.04_amd64_intel_21.iso", request.Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_WhenTheReleaseDisappeared_ReportsCatalogUnavailable()
    {
        var web = PopSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("popos", CancellationToken.None)).First(v => v.Id == "22.04/intel");
        web.Fail(Api + "22.04/intel", HttpStatusCode.NotFound);
        _time.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheApiIsDown_ReportCatalogUnavailableInsteadOfAnEmptyList()
    {
        var web = new FakeWeb().Fail(Api + "26.04/intel", HttpStatusCode.NotFound).Fail(Api + "24.04/intel", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("popos", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Products_ArePopOs_AndAnotherIsACallerMistake()
    {
        var provider = Provider(PopSite());

        Assert.Equal("popos", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("ubuntu", CancellationToken.None));
    }
}
