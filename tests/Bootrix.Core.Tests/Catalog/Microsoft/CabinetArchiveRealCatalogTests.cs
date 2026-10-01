// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Xml.Linq;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// The two cabinets are the real <c>products.cab</c> files behind Microsoft's go.microsoft.com links for Windows
/// 10 and 11. They use LZX with a 2 MiB window, which exercises Huffman trees, matches and repeated offsets over
/// thousands of frames. The expected digests come from 7-Zip's extraction of the same files.
/// </summary>
public class CabinetArchiveRealCatalogTests
{
    [Theory]
    [InlineData("products-win11-24h2.cab", 1_743_972, "86ef0b4c4f235d6d229019c46cce0d4b9287082acb98e9377d82b02b16a55b86")]
    [InlineData("products-win10-22h2.cab", 2_794_793, "76fcc8eeacb7da966441a7e0ac8b79cc095f13682abb92ee5a614c52f72ce54c")]
    public void Extract_RealCatalog_YieldsTheProductsXmlSevenZipProduces(string fixture, int length, string sha256)
    {
        var cabinet = CabinetArchive.Parse(MicrosoftFixtures.Bytes(fixture));

        var entry = Assert.Single(cabinet.Entries);
        Assert.Equal("products.xml", entry.Name);
        Assert.Equal(length, entry.Length);

        var xml = cabinet.Extract(entry);

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(xml)));
        Assert.Equal("MCT", XDocument.Load(new MemoryStream(xml)).Root!.Name.LocalName);
    }

    [RequiresToolFact("7z")]
    public void Extract_RealCatalog_EqualsLiveSevenZipOutput()
    {
        foreach (var fixture in new[] { "products-win11-24h2.cab", "products-win10-22h2.cab" })
        {
            var result = ExternalTools.Run("7z", "x", "-so", MicrosoftFixtures.PathOf(fixture));
            Assert.Equal(0, result.ExitCode);

            var cabinet = CabinetArchive.Parse(MicrosoftFixtures.Bytes(fixture));
            var ours = cabinet.Extract(Assert.Single(cabinet.Entries));

            Assert.Equal(System.Text.Encoding.UTF8.GetString(ours), result.Output);
        }
    }

    [Fact]
    public void Extract_FlippedBitInCompressedData_IsRejected()
    {
        var data = MicrosoftFixtures.Bytes("products-win11-24h2.cab");
        data[data.Length / 2] ^= 0x10;

        var cabinet = CabinetArchive.Parse(data);

        Assert.Throws<InvalidDataException>(() => cabinet.Extract(Assert.Single(cabinet.Entries)));
    }

    [Fact]
    public void Extract_TruncatedCatalog_IsRejected()
    {
        var data = MicrosoftFixtures.Bytes("products-win11-24h2.cab");

        var cabinet = CabinetArchive.Parse(data[..^1000]);

        Assert.Throws<InvalidDataException>(() => cabinet.Extract(Assert.Single(cabinet.Entries)));
    }

    [Fact]
    public void Extract_FolderLimit_RefusesDecompressionBombs()
    {
        var cabinet = CabinetArchive.Parse(MicrosoftFixtures.Bytes("products-win11-24h2.cab"), maxFolderSize: 1_000_000);

        Assert.Throws<InvalidDataException>(() => cabinet.Extract(Assert.Single(cabinet.Entries)));
    }
}
