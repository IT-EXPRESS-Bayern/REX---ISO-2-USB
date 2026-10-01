// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Net;

public class MetalinkDocumentTests
{
    private const string OpenSuseIso = "openSUSE-Leap-15.6-NET-x86_64-Build710.3-Media.iso";

    private static MetalinkFile OpenSuse() =>
        MetalinkDocument.Parse(NetFixtures.Text("metalink/opensuse-leap-15.6-net.iso.meta4")).Files.Single();

    [Fact]
    public void Metalink4ProvidesSizeHashesAndPieces()
    {
        var file = OpenSuse();

        Assert.Equal(OpenSuseIso, file.Name);
        Assert.Equal(273_678_336, file.Size);
        Assert.Equal(
            [HashKind.Md5, HashKind.Sha1, HashKind.Sha256, HashKind.Sha512],
            file.Hashes.Select(h => h.Kind));
        Assert.Equal("0984b36d0f420487f2766733ff8f9d779ade81d432b08e40e852a675313750bb", file.Hashes.Single(h => h.Kind == HashKind.Sha256).Hex);

        Assert.Equal(66, file.Pieces.Count);
        Assert.Equal(0, file.Pieces[0].Offset);
        Assert.Equal(4_194_304, file.Pieces[0].Length);
        Assert.Equal("34c1f60c00df8c25a565a78d53b97b0af2b68652", file.Pieces[0].Hash.Hex);
        Assert.Equal(HashKind.Sha1, file.Pieces[0].Hash.Kind);
        Assert.Equal(1_048_576, file.Pieces[^1].Length);
        Assert.Equal(file.Size, file.Pieces[^1].End);
    }

    [Fact]
    public void Metalink4MirrorsKeepPriorityAndLocation()
    {
        var file = OpenSuse();

        Assert.Equal(10, file.Mirrors.Count);
        Assert.Equal(Enumerable.Range(1, 10), file.Mirrors.Select(m => m.Priority));
        Assert.Equal("US", file.Mirrors[0].Location);
        Assert.Equal("CA", file.Mirrors[5].Location);
        Assert.All(file.Mirrors, m => Assert.Equal(0, m.MaxConnections));
        Assert.StartsWith("https://mirror.rackspace.com/", file.Mirrors[0].Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestUsesBestMirrorFirstAndSkipsMd5()
    {
        var request = OpenSuse().ToRequest();

        Assert.Equal(10, request.Sources.Count);
        Assert.Equal(request.Sources[0].Url, request.Url);
        Assert.DoesNotContain(request.ExpectedHashes, h => h.Kind == HashKind.Md5);
        Assert.Equal(3, request.ExpectedHashes.Count);
        Assert.Equal(273_678_336, request.ExpectedSize);
        Assert.Equal(66, request.Pieces.Count);
    }

    [Fact]
    public void PreferredLocationMovesItsMirrorsToTheFront()
    {
        var request = OpenSuse().ToRequest("ca");

        Assert.Equal("CA", request.Sources[0].Location);
        Assert.Equal(["US", "US", "US", "US", "US"], request.Sources.Skip(1).Take(5).Select(m => m.Location));
    }

    [Fact]
    public void RequestFromParsedMetalinkPassesValidation()
    {
        var request = OpenSuse().ToRequest();

        request.Validate();
    }

    [Fact]
    public void Metalink3HonoursMaxConnectionsAndDropsNonHttpMirrors()
    {
        var file = MetalinkDocument.Parse(NetFixtures.Text("metalink/fedora-repomd.metalink")).Files.Single();

        Assert.Equal("repomd.xml", file.Name);
        Assert.Equal(5966, file.Size);
        Assert.Equal(4, file.Hashes.Count);
        Assert.Equal(6, file.Mirrors.Count);
        Assert.All(file.Mirrors, m => Assert.Equal(1, m.MaxConnections));
        Assert.All(file.Mirrors, m => Assert.StartsWith("http", m.Url.Scheme, StringComparison.Ordinal));
        Assert.Empty(file.Pieces);
    }

    [Fact]
    public void Metalink3PreferenceBecomesPriorityWithBestFirst()
    {
        var file = MetalinkDocument.Parse(NetFixtures.Text("metalink/fedora-repomd.metalink")).Files.Single();

        Assert.Equal([1, 1, 2, 2, 3, 4], file.Mirrors.Select(m => m.Priority));
        Assert.Equal("cofractal-sea.mm.fcix.net", file.Mirrors[0].Url.Host);
    }

    [Fact]
    public void Metalink3PiecesAreOrderedByIndex()
    {
        var a = new string('1', 40);
        var b = new string('2', 40);
        var xml = $"""
            <metalink version="3.0" xmlns="http://www.metalinker.org/"><files><file name="x.bin">
              <size>300</size>
              <verification><pieces type="sha1" length="200"><hash piece="1">{b}</hash><hash piece="0">{a}</hash></pieces></verification>
              <resources><url type="https" preference="100" maxconnections="2">https://example.org/x.bin</url></resources>
            </file></files></metalink>
            """;

        var file = MetalinkDocument.Parse(xml).Files.Single();

        Assert.Equal([a, b], file.Pieces.Select(p => p.Hash.Hex));
        Assert.Equal([200, 100], file.Pieces.Select(p => p.Length));
        Assert.Equal(2, file.Mirrors.Single().MaxConnections);
    }

    [Fact]
    public void UnsupportedHashTypesAreIgnored()
    {
        var xml = $"""
            <metalink xmlns="urn:ietf:params:xml:ns:metalink"><file name="a.iso"><size>10</size>
              <hash type="sha-224">{new string('a', 56)}</hash>
              <hash type="sha-256">{new string('b', 64)}</hash>
              <url>https://example.org/a.iso</url></file></metalink>
            """;

        var file = MetalinkDocument.Parse(xml).Files.Single();

        Assert.Equal(HashKind.Sha256, Assert.Single(file.Hashes).Kind);
        Assert.Equal(999_999, file.Mirrors.Single().Priority);
    }

    [Fact]
    public void FindLooksUpByName()
    {
        var document = MetalinkDocument.Parse(NetFixtures.Text("metalink/opensuse-leap-15.6-net.iso.meta4"));

        Assert.NotNull(document.Find(OpenSuseIso));
        Assert.Null(document.Find("other.iso"));
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<html><body/></html>")]
    [InlineData("<metalink xmlns=\"urn:example:other\"><file name=\"a\"/></metalink>")]
    [InlineData("<metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"/>")]
    [InlineData("<metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file><size>1</size></file></metalink>")]
    [InlineData("<metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"a\"><size>ten</size></file></metalink>")]
    [InlineData("<metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"a\"><hash type=\"sha-256\">abc</hash></file></metalink>")]
    [InlineData("<metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"a\"><size>10</size><pieces length=\"4\" type=\"sha-1\"><hash>aa</hash></pieces></file></metalink>")]
    public void MalformedDocumentsRaiseMetalinkInvalid(string xml)
    {
        var ex = Assert.Throws<BootrixException>(() => MetalinkDocument.Parse(xml));

        Assert.Equal(ErrorCode.MetalinkInvalid, ex.Code);
    }

    [Fact]
    public void DoctypeDeclarationsAreRejected()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE metalink [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <metalink xmlns="urn:ietf:params:xml:ns:metalink"><file name="a"><size>1</size><url>https://example.org/&x;</url></file></metalink>
            """;

        var ex = Assert.Throws<BootrixException>(() => MetalinkDocument.Parse(xml));

        Assert.Equal(ErrorCode.MetalinkInvalid, ex.Code);
    }

    [Fact]
    public void EntryWithoutHttpMirrorCannotBecomeARequest()
    {
        var xml = """
            <metalink xmlns="urn:ietf:params:xml:ns:metalink"><file name="a"><size>1</size>
              <url>ftp://example.org/a</url><url>rsync://example.org/a</url></file></metalink>
            """;

        var file = MetalinkDocument.Parse(xml).Files.Single();

        var ex = Assert.Throws<BootrixException>(() => file.ToRequest());
        Assert.Equal(ErrorCode.MetalinkInvalid, ex.Code);
    }
}
