// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Wim;

public sealed class WimMetadataTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static WimMetadata ReadBytes(byte[] file) => WimMetadata.Read(new MemoryStream(file));

    // ---- fixture based tests (no external tools) ------------------------------------------------

    [Fact]
    public void Read_FixtureWithTwoImages_ParsesEveryField()
    {
        var xml = WimFixture.Xml(
            WimFixture.Image(1, "Windows 11 Pro", arch: 9, build: 26100),
            WimFixture.Image(2, "Windows 11 Home", arch: 9, build: 26100, edition: "Core", languages: "de-DE"));

        var wim = ReadBytes(WimFixture.Build(xml));

        Assert.Equal(2, wim.Editions.Count);
        var pro = wim.Editions[0];
        Assert.Equal(1, pro.Index);
        Assert.Equal("Windows 11 Pro", pro.Name);
        Assert.Equal("Windows 11 Pro description", pro.Description);
        Assert.Equal("Professional", pro.EditionId);
        Assert.Equal("Client", pro.InstallationType);
        Assert.Equal("Microsoft® Windows® Operating System", pro.ProductName);
        Assert.Equal(WindowsArch.X64, pro.Arch);
        Assert.Equal(26100, pro.Build);
        Assert.Equal(10, pro.MajorVersion);
        Assert.Equal(0, pro.MinorVersion);
        Assert.Equal(1, pro.ServicePackBuild);
        Assert.Equal(123456, pro.TotalBytes);
        Assert.Equal(9, pro.FileCount);
        Assert.Equal(3, pro.DirectoryCount);
        Assert.Equal(["en-US"], pro.Languages);
        Assert.Equal("en-US", pro.DefaultLanguage);
        Assert.Equal(new DateTimeOffset(DateTime.FromFileTimeUtc(0x01DD516BF82C261EL), TimeSpan.Zero), pro.Created);
        Assert.Equal("Core", wim.Editions[1].EditionId);

        Assert.Equal(WindowsArch.X64, wim.Arch);
        Assert.Equal(26100, wim.Build);
        Assert.Equal(["en-US", "de-DE"], wim.Languages);
        Assert.Equal(1000, wim.RecordedWimBytes);
        Assert.Equal(246912, wim.ImageBytes);
        Assert.False(wim.IsBootImage);
    }

    [Theory]
    [InlineData(0, WindowsArch.X86)]
    [InlineData(5, WindowsArch.Arm)]
    [InlineData(9, WindowsArch.X64)]
    [InlineData(12, WindowsArch.Arm64)]
    [InlineData(6, WindowsArch.Unknown)]
    public void Read_MapsProcessorArchitectureCodes(int code, WindowsArch expected)
    {
        var wim = ReadBytes(WimFixture.Build(WimFixture.Xml(WimFixture.Image(1, "x", code, 19045))));

        Assert.Equal(expected, wim.Arch);
        Assert.Equal(code, wim.Editions[0].ArchCode);
    }

    [Fact]
    public void Arch_WithMixedArchitectures_IsUnknown()
    {
        var xml = WimFixture.Xml(WimFixture.Image(1, "a", 0, 19045), WimFixture.Image(2, "b", 9, 19045));

        Assert.Equal(WindowsArch.Unknown, ReadBytes(WimFixture.Build(xml)).Arch);
    }

    [Fact]
    public void Read_BootImages_AreRecognised()
    {
        var xml = WimFixture.Xml(
            WimFixture.Image(1, "Microsoft Windows PE (x64)", 9, 26100, edition: "WindowsPE"),
            WimFixture.Image(2, "Microsoft Windows Setup (x64)", 9, 26100, edition: "WindowsPE"));

        Assert.True(ReadBytes(WimFixture.Build(xml)).IsBootImage);
    }

    [Fact]
    public void Read_ImageWithoutWindowsSection_HasNoVersionData()
    {
        var xml = "<WIM><IMAGE INDEX=\"1\"><NAME>Data</NAME><TOTALBYTES>5</TOTALBYTES></IMAGE></WIM>";

        var wim = ReadBytes(WimFixture.Build(xml));

        Assert.Equal(0, wim.Build);
        Assert.Equal(WindowsArch.Unknown, wim.Arch);
        Assert.Empty(wim.Languages);
        Assert.Null(wim.Editions[0].Created);
    }

    [Fact]
    public void Read_HeaderFields_AreDecoded()
    {
        var file = WimFixture.Build(WimFixture.Xml(WimFixture.Image(1, "a", 9, 1)), flags: 0x2008A, parts: 3, part: 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x78), 1);

        var header = ReadBytes(file).Header;

        Assert.Equal(0x10D00, header.Version);
        Assert.Equal(WimCompression.Xpress, header.Compression);
        Assert.Equal(32768, header.ChunkSize);
        Assert.Equal(2, header.PartNumber);
        Assert.Equal(3, header.TotalParts);
        Assert.True(header.IsSplit);
        Assert.Equal(1, header.ImageCount);
        Assert.Equal(1, header.BootIndex);
        Assert.Equal(Guid.Parse("11112222-3333-4444-5555-666677778888"), header.Id);
        Assert.False(header.IsSolid);
    }

    [Theory]
    [InlineData(0x2u | 0x40000u, WimCompression.Lzx)]
    [InlineData(0x2u | 0x80000u, WimCompression.Lzms)]
    [InlineData(0x2u | 0x20000u, WimCompression.Xpress)]
    [InlineData(0u, WimCompression.None)]
    public void Header_CompressionComesFromTheFlags(uint flags, WimCompression expected)
    {
        var header = WimHeader.Parse(WimFixture.Build(WimFixture.Xml(), flags));

        Assert.Equal(expected, header.Compression);
    }

    [Fact]
    public void Read_HeaderWithoutXml_ReturnsNoEditions()
    {
        var wim = ReadBytes(WimFixture.Build(WimFixture.Xml(), includeXml: false));

        Assert.Empty(wim.Editions);
        Assert.Equal(0, wim.Build);
    }

    [Fact]
    public void Read_NotAWim_IsRejected()
    {
        var file = WimFixture.Build(WimFixture.Xml());
        file[0] = (byte)'X';

        var ex = Assert.Throws<BootrixException>(() => ReadBytes(file));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public void Read_FileShorterThanHeader_IsRejected()
    {
        var ex = Assert.Throws<BootrixException>(() => ReadBytes(WimFixture.Build(WimFixture.Xml())[..100]));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public void Read_UnexpectedHeaderSize_IsUnsupported()
    {
        var file = WimFixture.Build(WimFixture.Xml());
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 176);

        Assert.Equal(ErrorCode.ImageUnsupported, Assert.Throws<BootrixException>(() => ReadBytes(file)).Code);
    }

    [Fact]
    public void Read_XmlBeyondTheEndOfTheFile_IsReportedAsTruncated()
    {
        var file = WimFixture.Build(WimFixture.Xml(WimFixture.Image(1, "a", 9, 1)));

        var ex = Assert.Throws<BootrixException>(() => ReadBytes(file[..^40]));

        Assert.Equal(ErrorCode.ImageTruncated, ex.Code);
    }

    [Fact]
    public void Read_CompressedXmlResource_IsUnsupported()
    {
        var file = WimFixture.Build(WimFixture.Xml(), xmlResourceFlags: 0x06);

        Assert.Equal(ErrorCode.ImageUnsupported, Assert.Throws<BootrixException>(() => ReadBytes(file)).Code);
    }

    [Fact]
    public void Read_MalformedXml_IsRejected()
    {
        var file = WimFixture.Build("<WIM><IMAGE INDEX=\"1\">");

        Assert.Equal(ErrorCode.ImageUnreadable, Assert.Throws<BootrixException>(() => ReadBytes(file)).Code);
    }

    [Fact]
    public void Read_XmlWithDoctype_IsRejected()
    {
        var file = WimFixture.Build("<!DOCTYPE WIM [<!ENTITY x \"y\">]><WIM>&x;</WIM>");

        Assert.Equal(ErrorCode.ImageUnreadable, Assert.Throws<BootrixException>(() => ReadBytes(file)).Code);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheSameAsRead()
    {
        var file = WimFixture.Build(WimFixture.Xml(WimFixture.Image(1, "Windows 10 Pro", 0, 19045)));

        var wim = await WimMetadata.ReadAsync(new MemoryStream(file));

        Assert.Equal(WindowsArch.X86, wim.Arch);
        Assert.Equal(19045, wim.Build);
    }

    // ---- real files written by wimlib, verified against wiminfo ---------------------------------

    private string Capture(string name, string imageName, string compression, string[] properties, string[]? extra = null)
    {
        var tree = Path.Combine(_dir.Path, "tree-" + name);
        Directory.CreateDirectory(Path.Combine(tree, "Windows"));
        File.WriteAllText(Path.Combine(tree, "Windows", "a.txt"), "hello " + name);
        File.WriteAllBytes(Path.Combine(tree, "Windows", "zeros.bin"), new byte[200_000]);
        var wim = _dir.File(name + ".wim");
        ReferenceTool.Run("wimcapture", [tree, wim, imageName, imageName + " description", "--compress=" + compression], null, null, null);
        if (properties.Length > 0)
        {
            var arguments = new List<string> { wim, "1" };
            foreach (var property in properties)
            {
                arguments.Add("--image-property");
                arguments.Add(property);
            }

            ReferenceTool.Run("wiminfo", arguments, null, null, null);
        }

        if (extra is not null)
        {
            ReferenceTool.Run("wimappend", [Path.Combine(_dir.Path, "tree-" + name), wim, extra[0], extra[1]], null, null, null);
        }

        return wim;
    }

    private static readonly string[] WindowsProperties =
    [
        "WINDOWS/ARCH=9", "WINDOWS/EDITIONID=Professional", "WINDOWS/VERSION/BUILD=26100", "WINDOWS/VERSION/MAJOR=10",
        "WINDOWS/VERSION/MINOR=0", "WINDOWS/VERSION/SPBUILD=1", "WINDOWS/LANGUAGES/LANGUAGE=en-US",
        "WINDOWS/LANGUAGES/DEFAULT=en-US", "WINDOWS/INSTALLATIONTYPE=Client",
        "WINDOWS/PRODUCTNAME=Microsoft® Windows® Operating System",
    ];

    /// <summary>Parses the "Key:   value" blocks of <c>wiminfo</c> into one dictionary per image.</summary>
    private static List<Dictionary<string, string>> WimInfoImages(string path)
    {
        var text = ReferenceTool.Run("wiminfo", path).StandardOutput;
        var images = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')).SkipWhile(l => !l.StartsWith("Available Images", StringComparison.Ordinal)).Skip(2))
        {
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    images.Add(current);
                    current = [];
                }

                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            current[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (current.Count > 0)
        {
            images.Add(current);
        }

        return images;
    }

    private static WimMetadata ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return WimMetadata.Read(stream);
    }

    [ToolTheory("wimcapture", "wiminfo", "wimappend")]
    [InlineData("LZX")]
    [InlineData("XPRESS")]
    [InlineData("none")]
    public void Read_WimlibFile_MatchesWiminfoForEveryImage(string compression)
    {
        var wim = Capture("w-" + compression, "Windows 11 Pro", compression, WindowsProperties, ["Windows 11 Home", "Home edition"]);

        var metadata = ReadFile(wim);
        var reference = WimInfoImages(wim);

        Assert.Equal(reference.Count, metadata.Editions.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            var expected = reference[i];
            var actual = metadata.Editions[i];
            Assert.Equal(int.Parse(expected["Index"], CultureInfo.InvariantCulture), actual.Index);
            Assert.Equal(expected["Name"], actual.Name);
            Assert.Equal(expected["Description"], actual.Description);
            Assert.Equal(long.Parse(expected["Total Bytes"], CultureInfo.InvariantCulture), actual.TotalBytes);
            Assert.Equal(long.Parse(expected["File Count"], CultureInfo.InvariantCulture), actual.FileCount);
            Assert.Equal(expected.GetValueOrDefault("Edition ID"), actual.EditionId);
            Assert.Equal(expected.GetValueOrDefault("Product Name"), actual.ProductName);
            Assert.Equal(expected.GetValueOrDefault("Installation Type"), actual.InstallationType);
            Assert.Equal(expected.GetValueOrDefault("Default Language"), actual.DefaultLanguage);
            Assert.Equal(expected.TryGetValue("Build", out var build) ? int.Parse(build, CultureInfo.InvariantCulture) : 0, actual.Build);
            Assert.Equal(expected.TryGetValue("Major Version", out var major) ? int.Parse(major, CultureInfo.InvariantCulture) : 0, actual.MajorVersion);
            Assert.Equal(expected.TryGetValue("Service Pack Build", out var sp) ? int.Parse(sp, CultureInfo.InvariantCulture) : 0, actual.ServicePackBuild);
            Assert.Equal(expected.TryGetValue("Architecture", out var arch) && arch == "x86_64" ? WindowsArch.X64 : WindowsArch.Unknown, actual.Arch);
            Assert.Equal(
                expected.GetValueOrDefault("Languages", string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                actual.Languages);
        }

        var info = ReferenceTool.Run("wiminfo", wim).StandardOutput;
        var expectedCompression = compression switch { "LZX" => WimCompression.Lzx, "XPRESS" => WimCompression.Xpress, _ => WimCompression.None };
        Assert.Equal(expectedCompression, metadata.Header.Compression);
        Assert.Contains($"Image Count:    {metadata.Header.ImageCount}", info, StringComparison.Ordinal);
        Assert.Contains($"Version:        {metadata.Header.Version}", info, StringComparison.Ordinal);
        Assert.Contains("Part Number:    1/1", info, StringComparison.Ordinal);
        Assert.True(metadata.RecordedWimBytes > 0);
        Assert.Equal(2, metadata.Header.ImageCount);
        Assert.Equal(26100, metadata.Build);
        Assert.Equal(["en-US"], metadata.Languages);
        Assert.Equal(WindowsArch.X64, metadata.Arch);
    }

    [ToolFact("wimcapture", "wiminfo")]
    public void Read_WimlibFileWithOneWindowsImage_ReportsArchitectureAndBuild()
    {
        var wim = Capture("single", "Windows 11 Pro", "LZX", WindowsProperties);

        var metadata = ReadFile(wim);

        Assert.Equal(WindowsArch.X64, metadata.Arch);
        Assert.Equal(26100, metadata.Build);
        Assert.Equal("en-US", metadata.DefaultLanguage);
        Assert.Equal(WimCompression.Lzx, metadata.Header.Compression);
        Assert.Equal(new FileInfo(wim).Length, metadata.Header.ExpectedLength);
    }

    [ToolTheory("wimcapture", "wiminfo")]
    [InlineData("WINDOWS/ARCH=0", WindowsArch.X86)]
    [InlineData("WINDOWS/ARCH=12", WindowsArch.Arm64)]
    [InlineData("WINDOWS/ARCH=5", WindowsArch.Arm)]
    public void Read_WimlibFile_MapsOtherArchitectures(string property, WindowsArch expected)
    {
        var wim = Capture("arch" + expected, "Windows", "none", [property, "WINDOWS/VERSION/BUILD=19045"]);

        Assert.Equal(expected, ReadFile(wim).Arch);
    }

    [ToolFact("wimcapture", "wiminfo", "wimexport")]
    public void Read_SolidEsd_IsRecognisedAndListsAllImages()
    {
        var wim = Capture("source", "Windows 11 Pro", "LZX", WindowsProperties, ["Windows 11 Home", "Home edition"]);
        var esd = _dir.File("image.esd");
        ReferenceTool.Run("wimexport", [wim, "all", esd, "--solid"], null, null, null);

        var metadata = ReadFile(esd);

        Assert.True(metadata.Header.IsSolid);
        Assert.Equal(WimCompression.Lzms, metadata.Header.Compression);
        Assert.Equal(2, metadata.Editions.Count);
        Assert.Equal(26100, metadata.Build);
        Assert.Equal("Windows 11 Home", metadata.Editions[1].Name);
    }

    [ToolFact("wimcapture", "wiminfo", "wimsplit")]
    public void Read_SplitParts_EachCarryTheImageData()
    {
        var tree = Path.Combine(_dir.Path, "split-tree");
        Directory.CreateDirectory(tree);
        for (var i = 0; i < 4; i++)
        {
            var random = new byte[1_200_000];
            new Random(3 + i).NextBytes(random);
            File.WriteAllBytes(Path.Combine(tree, $"random{i}.bin"), random);
        }

        var wim = _dir.File("big.wim");
        ReferenceTool.Run("wimcapture", [tree, wim, "Windows 10 Pro", "Pro", "--compress=XPRESS"], null, null, null);
        ReferenceTool.Run("wiminfo", [wim, "1", "--image-property", "WINDOWS/ARCH=0", "--image-property", "WINDOWS/VERSION/BUILD=19045"], null, null, null);
        ReferenceTool.Run("wimsplit", [wim, _dir.File("part.swm"), "1"], null, null, null);

        var parts = Directory.GetFiles(_dir.Path, "part*.swm").Order().Select(ReadFile).ToList();

        Assert.True(parts.Count >= 3, $"wimsplit produced {parts.Count} parts");
        Assert.All(parts, part =>
        {
            Assert.True(part.Header.IsSplit);
            Assert.Equal(parts.Count, part.Header.TotalParts);
            Assert.Equal(WindowsArch.X86, part.Arch);
            Assert.Equal(19045, part.Build);
            Assert.Equal(parts[0].Header.Id, part.Header.Id);
        });
        Assert.Equal(Enumerable.Range(1, parts.Count), parts.Select(part => part.Header.PartNumber));
    }

    [ToolFact("wimcapture", "wiminfo")]
    public void Read_WimlibFile_CutBeforeItsLookupTable_IsTruncated()
    {
        var wim = Capture("cut", "Windows 11 Pro", "LZX", WindowsProperties);
        var length = new FileInfo(wim).Length;
        using (var file = new FileStream(wim, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(length - 100);
        }

        var ex = Assert.Throws<BootrixException>(() => ReadFile(wim));

        Assert.Equal(ErrorCode.ImageTruncated, ex.Code);
    }
}
