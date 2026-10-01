// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Tests.Profiles;

public class ProfilePackageTests
{
    private static byte[] Build(Dictionary<string, byte[]>? assets = null)
    {
        using var stream = new MemoryStream();
        ProfilePackage.Write(stream, new ProfileFile { Name = "demo", Description = "test" }, assets);
        return stream.ToArray();
    }

    [Fact]
    public void RoundTripKeepsProfileAndAssets()
    {
        var bytes = Build(new() { ["logo.png"] = [1, 2, 3] });

        var opened = ProfilePackage.Read(new MemoryStream(bytes));

        Assert.Equal("demo", opened.Profile.Name);
        Assert.Equal([1, 2, 3], opened.Assets["logo.png"]);
    }

    [Fact]
    public void ModifiedAssetIsRejected()
    {
        var bytes = Build(new() { ["logo.png"] = [1, 2, 3] });
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("assets/logo.png")!.Delete();
            using var replaced = zip.CreateEntry("assets/logo.png").Open();
            replaced.Write([9, 9, 9]);
        }

        stream.Position = 0;
        var ex = Assert.Throws<BootrixException>(() => ProfilePackage.Read(stream));

        Assert.Equal(ErrorCode.ProfilePackageCorrupt, ex.Code);
    }

    [Fact]
    public void UnlistedFileIsRejected()
    {
        var bytes = Build();
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var extra = zip.CreateEntry("assets/evil.cmd").Open();
            extra.Write(Encoding.UTF8.GetBytes("format c:"));
        }

        stream.Position = 0;

        Assert.Throws<BootrixException>(() => ProfilePackage.Read(stream));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("C:/evil.txt")]
    public void PathTraversalInAssetNamesIsRejected(string name)
    {
        using var stream = new MemoryStream();

        Assert.Throws<BootrixException>(() =>
            ProfilePackage.Write(stream, new ProfileFile { Name = "x" }, new Dictionary<string, byte[]> { [name] = [1] }));
    }

    [Fact]
    public void ArchiveWithoutManifestIsRejected()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("profile.json");
        }

        stream.Position = 0;

        Assert.Throws<BootrixException>(() => ProfilePackage.Read(stream));
    }
}
