// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Net;

public class FileVersionStoreTests
{
    [Fact]
    public void UnknownChannelStartsAtZero()
    {
        using var dir = new TempDirectory();

        Assert.Equal(0, new FileVersionStore(dir.File("versions.json")).GetHighestVersion("catalog"));
    }

    [Fact]
    public void RecordedVersionSurvivesANewInstance()
    {
        using var dir = new TempDirectory();
        new FileVersionStore(dir.File("versions.json")).Record("catalog", 12);

        Assert.Equal(12, new FileVersionStore(dir.File("versions.json")).GetHighestVersion("catalog"));
    }

    [Fact]
    public void ValueOnlyEverGoesUp()
    {
        using var dir = new TempDirectory();
        var store = new FileVersionStore(dir.File("versions.json"));

        store.Record("catalog", 10);
        store.Record("catalog", 4);
        store.Record("catalog", 10);

        Assert.Equal(10, store.GetHighestVersion("catalog"));
    }

    [Fact]
    public void ChannelsAreIndependent()
    {
        using var dir = new TempDirectory();
        var store = new FileVersionStore(dir.File("versions.json"));

        store.Record("catalog", 3);
        store.Record("revocation", 40);

        Assert.Equal(3, store.GetHighestVersion("catalog"));
        Assert.Equal(40, store.GetHighestVersion("revocation"));
    }

    [Fact]
    public void FolderIsCreatedAndNoTemporaryFileIsLeftBehind()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "settings", "deep", "versions.json");

        new FileVersionStore(path).Record("catalog", 1);

        Assert.Equal(["versions.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void UnreadableFileCountsAsEmptyAndIsRepairedOnTheNextRecord(string content)
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("versions.json"), content);
        var store = new FileVersionStore(dir.File("versions.json"));

        Assert.Equal(0, store.GetHighestVersion("catalog"));
        store.Record("catalog", 2);

        Assert.Equal(2, new FileVersionStore(dir.File("versions.json")).GetHighestVersion("catalog"));
    }

    [Fact]
    public void ConcurrentRecordsKeepTheHighestValue()
    {
        using var dir = new TempDirectory();
        var store = new FileVersionStore(dir.File("versions.json"));

        Parallel.For(1, 101, version => store.Record("catalog", version));

        Assert.Equal(100, store.GetHighestVersion("catalog"));
        Assert.Equal(100, new FileVersionStore(dir.File("versions.json")).GetHighestVersion("catalog"));
    }
}
