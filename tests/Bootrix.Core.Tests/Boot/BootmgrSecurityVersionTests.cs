// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// The SVN lives in the RCDATA resource BOOTMGRSECURITYVERSIONNUMBER of bootmgfw.efi (Rufus and the Check-UEFISecureBootVariables
/// scripts read it the same way). A real boot manager with that resource was not available for these tests, so the
/// resource tree is built synthetically in the structure those tools expect: RT_RCDATA, named resource, language, four bytes.
/// </summary>
public class BootmgrSecurityVersionTests
{
    [Theory]
    [InlineData(9, 0)]
    [InlineData(7, 0)]
    [InlineData(11, 3)]
    [InlineData(65535, 65535)]
    public void ReadBootmgrSecurityVersion_ReadsMajorAndMinor(int major, int minor)
    {
        var image = PeBuilder.Typical().AddBootmgrSecurityVersion((ushort)major, (ushort)minor).Build();

        var version = EfiBinary.Parse(image).ReadBootmgrSecurityVersion();

        Assert.Equal(new SecurityVersion((ushort)major, (ushort)minor), version);
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_MatchesTheEncodingOfTheDbxEntry()
    {
        // The DBX value 0x...0000 0900 for "9.0" is the same little-endian pair as the resource: minor first.
        var image = PeBuilder.Typical().AddBootmgrSecurityVersion(9, 0).Build();
        var binary = EfiBinary.Parse(image);

        Assert.True(binary.TryGetSectionData(".rsrc", out var resources));
        Assert.Equal([0x00, 0x00, 0x09, 0x00], resources.Span[^4..].ToArray());
        Assert.Equal("9.0", binary.ReadBootmgrSecurityVersion()?.ToString());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_NameIsMatchedWithoutRegardToCase()
    {
        var image = PeBuilder.Typical().AddBootmgrSecurityVersion(8, 0, "bootmgrsecurityversionnumber").Build();

        Assert.Equal(new SecurityVersion(8, 0), EfiBinary.Parse(image).ReadBootmgrSecurityVersion());
    }

    [Theory]
    [InlineData("SECURITYVERSIONNUMBER")]
    [InlineData("OTHER")]
    [InlineData("BOOTMGRSECURITYVERSIONNUMBERX")]
    public void ReadBootmgrSecurityVersion_OtherResourceNames_ReturnNull(string name)
    {
        var image = PeBuilder.Typical().AddBootmgrSecurityVersion(9, 0, name).Build();

        Assert.Null(EfiBinary.Parse(image).ReadBootmgrSecurityVersion());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_ImageWithoutResources_ReturnsNull()
    {
        Assert.Null(EfiBinary.Parse(PeBuilder.Typical().Build()).ReadBootmgrSecurityVersion());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_ResourceDirectoryEntryPastTheSection_ReturnsNull()
    {
        var tree = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(16), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(20), 0x80000000u | 0x7FFFFFF0u);

        var image = new PeBuilder { RawResourceSection = tree }.AddSection(".text", [1, 2, 3]).Build();

        Assert.Null(EfiBinary.Parse(image).ReadBootmgrSecurityVersion());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_DirectoryThatPointsToItself_ReturnsNull()
    {
        var tree = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(16), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(20), 0x80000000u); // back to the root

        var image = new PeBuilder { RawResourceSection = tree }.AddSection(".text", [1, 2, 3]).Build();

        Assert.Null(EfiBinary.Parse(image).ReadBootmgrSecurityVersion());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_HugeEntryCounts_AreBounded()
    {
        var tree = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(12), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(14), 0xFFFF);

        var image = new PeBuilder { RawResourceSection = tree }.AddSection(".text", [1, 2, 3]).Build();

        Assert.Null(EfiBinary.Parse(image).ReadBootmgrSecurityVersion());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_DataOfWrongSize_ReturnsNull()
    {
        var image = PeBuilder.Typical().AddBootmgrSecurityVersion(9, 0).Build();
        var section = EfiBinary.Parse(image).Sections.Single(s => s.Name == ".rsrc");

        // The data entry sits at offset 72 of the tree, its size field four bytes further.
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)section.RawOffset + 72 + 4), 2);

        Assert.Null(EfiBinary.Parse(image).ReadBootmgrSecurityVersion());
    }

    [Fact]
    public void ReadBootmgrSecurityVersion_RandomlyDamagedResources_NeverThrowAnythingButBootrixException()
    {
        var original = PeBuilder.Typical().AddBootmgrSecurityVersion(9, 0).Build();
        var start = (int)EfiBinary.Parse(original).Sections.Single(s => s.Name == ".rsrc").RawOffset;
        var random = new Random(7);

        for (var round = 0; round < 2000; round++)
        {
            var copy = (byte[])original.Clone();
            for (var change = 0; change < 3; change++)
            {
                copy[start + random.Next(120)] = (byte)random.Next(256);
            }

            try
            {
                EfiBinary.Parse(copy).ReadBootmgrSecurityVersion();
            }
            catch (BootrixException ex)
            {
                Assert.Equal(ErrorCode.EfiBinaryInvalid, ex.Code);
            }
        }
    }

    [Fact]
    public void SecurityVersion_ComparesMajorBeforeMinor()
    {
        Assert.True(new SecurityVersion(7, 0) < new SecurityVersion(9, 0));
        Assert.True(new SecurityVersion(9, 1) > new SecurityVersion(9, 0));
        Assert.True(new SecurityVersion(8, 5) < new SecurityVersion(9, 0));
        Assert.True(new SecurityVersion(9, 0) >= new SecurityVersion(9, 0));
        Assert.True(new SecurityVersion(2, 9) <= new SecurityVersion(3, 0));
    }

    [Theory]
    [InlineData("9.0", true, 9, 0)]
    [InlineData("3.12", true, 3, 12)]
    [InlineData("9", false, 0, 0)]
    [InlineData("9.x", false, 0, 0)]
    [InlineData("-1.0", false, 0, 0)]
    [InlineData("70000.0", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    public void SecurityVersion_TryParse_AcceptsMajorDotMinorOnly(string? text, bool ok, int major, int minor)
    {
        Assert.Equal(ok, SecurityVersion.TryParse(text, out var version));
        Assert.Equal(new SecurityVersion((ushort)major, (ushort)minor), version);
    }
}
