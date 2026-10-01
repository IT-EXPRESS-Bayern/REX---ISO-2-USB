// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>Images that are recognised but not supported must fail with a specific, translatable error.</summary>
public class DmgRejectionTests
{
    private static BootrixException OpenFails(byte[] image) =>
        Assert.Throws<BootrixException>(() => DmgReader.Open(new MemoryStream(image)));

    [Fact]
    public void EncryptedVersion2_IsRejectedAsEncrypted()
    {
        var image = new byte[4096];
        "encrcdsa"u8.CopyTo(image);

        Assert.Equal(ErrorCode.ImageEncrypted, OpenFails(image).Code);
    }

    [Fact]
    public void EncryptedVersion1_WithHeaderAtTheEnd_IsRejectedAsEncrypted()
    {
        var image = new byte[20000];
        "cdsaencr"u8.CopyTo(image.AsSpan(image.Length - 200));

        Assert.Equal(ErrorCode.ImageEncrypted, OpenFails(image).Code);
    }

    [Fact]
    public void AppleEncryptedArchive_IsRejected()
    {
        var image = new byte[4096];
        "AEA1"u8.CopyTo(image);

        Assert.Equal(ErrorCode.ImageAppleArchive, OpenFails(image).Code);
    }

    [Fact]
    public void SegmentedImage_IsRejected()
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder
        {
            MutateTrailer = t =>
            {
                BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x38), 1);
                BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x3C), 3);
            },
        });

        Assert.Equal(ErrorCode.ImageSegmented, OpenFails(image).Code);
    }

    [Fact]
    public void DiskCopy42Image_IsRejectedAsLegacy()
    {
        var image = new byte[0x54 + 1474560];
        image[0] = 8;
        "DISKNAME"u8.CopyTo(image.AsSpan(1));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x40), 1474560);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x52), 0x0100);

        Assert.Equal(ErrorCode.ImageLegacyFormat, OpenFails(image).Code);
    }

    [Fact]
    public void NdifMacBinaryFile_IsRejectedAsLegacy()
    {
        var image = new byte[4096];
        image[1] = 4;
        "test"u8.CopyTo(image.AsSpan(2));
        "rohd"u8.CopyTo(image.AsSpan(65));
        "ddsk"u8.CopyTo(image.AsSpan(69));

        Assert.Equal(ErrorCode.ImageLegacyFormat, OpenFails(image).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(100_000)]
    public void FileWithoutTrailer_IsReportedAsDamagedOrIncomplete(int length)
    {
        var image = ImageTestData.Random(length, 3);

        var ex = OpenFails(image);

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Fact]
    public void ImageWithTrailerCutOff_IsReportedAsDamagedOrIncomplete()
    {
        var image = DmgFixtures.Build("zlib");

        var ex = OpenFails(image[..^100]);

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Fact]
    public void ImageWithPayloadCutOut_IsReportedAsTruncated()
    {
        var image = DmgFixtures.Build("zlib");
        // Remove bytes from the middle of the data fork: the trailer stays, the offsets now point past the end.
        var damaged = image.Take(5000).Concat(image.Skip(60_000)).ToArray();

        var ex = OpenFails(damaged);

        Assert.Equal(ErrorCode.ImageTruncated, ex.Code);
    }

    [Fact]
    public void UnknownTrailerVersion_IsRejectedAsUnsupported()
    {
        var image = DmgFixtures.Build("zlib");
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(image.Length - 512 + 4), 5);

        Assert.Equal(ErrorCode.ImageUnsupported, OpenFails(image).Code);
    }

    [Fact]
    public void UnknownChunkType_IsRejectedAsUnsupported()
    {
        var image = new UdifBuilder
        {
            MutateTable = (_, table) =>
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0xCC), 0x80000042);
                return table;
            },
        }.AddPartition(0, "x (Apple_HFS : 1)", 0, [ChunkSpec.Zlib(ImageTestData.Text(4096, 1))]).Build();

        Assert.Equal(ErrorCode.ImageUnsupported, OpenFails(image).Code);
    }

    [Fact]
    public void EveryRejectionHasLocalizedGermanAndEnglishText()
    {
        foreach (var code in new[]
        {
            ErrorCode.ImageEncrypted, ErrorCode.ImageAppleArchive, ErrorCode.ImageSegmented,
            ErrorCode.ImageLegacyFormat, ErrorCode.ImageCorrupt, ErrorCode.ImageTruncated, ErrorCode.ImageUnsupported,
        })
        {
            foreach (var culture in new[] { "de", "en" })
            {
                var localizer = new Bootrix.Core.Localization.Localizer { Culture = System.Globalization.CultureInfo.GetCultureInfo(culture) };
                var description = ErrorCatalog.Describe(code, localizer, "detail", "detail");
                Assert.DoesNotContain("Error.", description.Cause, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(description.Action));
            }
        }
    }

    [Fact]
    public void RejectionMessage_MentionsTheFormat()
    {
        var image = new byte[4096];
        "encrcdsa"u8.CopyTo(image);

        var ex = OpenFails(image);

        Assert.Contains("encrcdsa", ex.Message, StringComparison.Ordinal);
    }
}
