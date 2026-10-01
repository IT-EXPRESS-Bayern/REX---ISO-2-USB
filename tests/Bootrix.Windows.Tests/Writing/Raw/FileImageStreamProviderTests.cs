// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Writing.Raw;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Tests.Writing.Raw;

public sealed class FileImageStreamProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-provider-" + Guid.NewGuid().ToString("N"));

    public FileImageStreamProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static byte[] Content(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private string Gzip(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(content);
        return path;
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task APlainFile_IsHandedOutAsItIs()
    {
        var content = Content(300_000);
        var path = Path.Combine(_directory, "a.iso");
        await File.WriteAllBytesAsync(path, content);

        await using var image = await new FileImageStreamProvider().OpenAsync(path, default);

        Assert.Equal(content.Length, image.Length);
        Assert.Equal(ImageSourceKind.File, image.Source!.Kind);
        Assert.Equal(content, await ReadAll(image.Stream));
    }

    [Fact]
    public async Task AGzipFile_IsDecodedAndItsLengthIsUnknown()
    {
        var content = Content(500_000);
        var path = Gzip("a.img.gz", content);

        await using var image = await new FileImageStreamProvider().OpenAsync(path, default);

        Assert.Null(image.Length);
        Assert.Equal(CompressionFormat.GZip, image.Source!.Compression);
        Assert.Equal(content, await ReadAll(image.Stream));
    }

    [Fact]
    public async Task ForInspection_ACompressedFileComesBackAsTheFile()
    {
        var path = Gzip("b.img.gz", Content(100_000));

        await using var image = await new FileImageStreamProvider().OpenForInspectionAsync(path, new ImageOpenOptions(), default);

        Assert.True(image.Stream.CanSeek);
        Assert.Equal(new FileInfo(path).Length, image.Length);
    }

    [Fact]
    public async Task TheArchiveEntryIsPassedOn()
    {
        var path = Path.Combine(_directory, "two.zip");
        var wanted = Content(20_000);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "big.iso", Content(90_000));
            Add(archive, "small.img", wanted);
        }

        await using var image = await new FileImageStreamProvider().OpenAsync(path, new ImageOpenOptions { ArchiveEntry = "small.img" }, default);

        Assert.Equal("small.img", image.Source!.ArchiveEntry);
        Assert.Equal(wanted, await ReadAll(image.Stream));
    }

    [Fact]
    public async Task ABlockMapNextToTheImage_ComesWithIt()
    {
        var path = Path.Combine(_directory, "m.img");
        await File.WriteAllBytesAsync(path, new byte[8192]);
        await File.WriteAllTextAsync(path + ".bmap", """
            <?xml version="1.0" ?>
            <bmap version="2.0">
                <ImageSize> 8192 </ImageSize>
                <BlockSize> 4096 </BlockSize>
                <BlocksCount> 2 </BlocksCount>
                <MappedBlocksCount> 1 </MappedBlocksCount>
                <ChecksumType> sha256 </ChecksumType>
                <BlockMap>
                    <Range> 0 </Range>
                </BlockMap>
            </bmap>
            """);

        await using var image = await new FileImageStreamProvider().OpenAsync(path, default);
        await using var off = await new FileImageStreamProvider().OpenAsync(path, new ImageOpenOptions { BlockMap = BlockMapUse.Off }, default);

        Assert.Equal(1, image.Source!.BlockMap!.MappedBlocksCount);
        Assert.Null(off.Source!.BlockMap);
    }

    [Fact]
    public async Task ASparseBundle_IsRefusedUnlessTheProviderRunsAsTheUser()
    {
        var bundle = Path.Combine(_directory, "disk.sparsebundle");
        Directory.CreateDirectory(Path.Combine(bundle, "bands"));
        await File.WriteAllTextAsync(Path.Combine(bundle, "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            <key>band-size</key><integer>4096</integer>
            <key>size</key><integer>8192</integer>
            <key>diskimage-bundle-type</key><string>com.apple.diskimage.sparsebundle</string>
            </dict></plist>
            """);
        await File.WriteAllBytesAsync(Path.Combine(bundle, "bands", "0"), Content(4096));

        var refused = await Assert.ThrowsAsync<BootrixException>(() => new FileImageStreamProvider().OpenAsync(bundle, default));
        await using var allowed = await new FileImageStreamProvider(allowSparseBundles: true).OpenAsync(bundle, default);

        Assert.Equal(ErrorCode.ImageUnsupported, refused.Code);
        Assert.Equal(8192, allowed.Length);
    }

    [Fact]
    public async Task ImpersonatingProvider_PassesTheOptionsAndTheInspectionOpenThroughTheClientsScope()
    {
        var scopes = 0;
        var inner = new Recording();
        var provider = new ImpersonatingImageStreamProvider(inner, new Counting(() => scopes++));

        await using var a = await provider.OpenAsync("x.img", new ImageOpenOptions { ArchiveEntry = "e" }, default);
        await using var b = await provider.OpenForInspectionAsync("y.img", new ImageOpenOptions(), default);

        Assert.Equal(2, scopes);
        Assert.Equal(["open:x.img:e", "inspect:y.img:"], inner.Calls);
    }

    private sealed class Counting(Action onScope) : IClientImpersonator
    {
        public Task<T> RunAsClientAsync<T>(Func<Task<T>> action)
        {
            onScope();
            return action();
        }
    }

    private sealed class Recording : IImageStreamProvider
    {
        public List<string> Calls { get; } = [];

        public Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken) => Record("plain", path, null);

        public Task<OpenedImage> OpenAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken) => Record("open", path, options.ArchiveEntry);

        public Task<OpenedImage> OpenForInspectionAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken) => Record("inspect", path, options.ArchiveEntry);

        private Task<OpenedImage> Record(string kind, string path, string? entry)
        {
            Calls.Add($"{kind}:{path}:{entry}");
            return Task.FromResult(new OpenedImage(new MemoryStream(), 0));
        }
    }
}
