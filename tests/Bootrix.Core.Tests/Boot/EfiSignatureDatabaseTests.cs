// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CA5350 // SHA-1 only identifies certificates by their published thumbprints
using System.Buffers.Binary;
using System.Security.Cryptography;
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// The parser is tested with the signed DBX updates of microsoft/secureboot_objects (small real files) and with lists
/// constructed in <see cref="DbxBuilder"/> for the cases that the real files do not contain.
/// </summary>
public class EfiSignatureDatabaseTests
{
    private static readonly Guid MicrosoftOwner = new("77fa9abd-0359-4d32-bd60-28f4e78f784b");

    private static void AssertInvalid(byte[] data)
    {
        var ex = Assert.Throws<BootrixException>(() => EfiSignatureDatabase.Parse(data));
        Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
    }

    [Fact]
    public void Parse_RealAmd64DbxUpdate_ReadsAllHashes()
    {
        var lists = EfiSignatureDatabase.Parse(Fixtures.Read("DBXUpdate.amd64.bin"));

        var list = Assert.Single(lists);
        Assert.Equal(EfiSignatureType.Sha256, list.Type);
        Assert.Equal(443, list.Entries.Count);
        Assert.All(list.Entries, e =>
        {
            Assert.Equal(MicrosoftOwner, e.Owner);
            Assert.Equal(32, e.Data.Length);
        });
        Assert.Empty(list.Header.ToArray());
    }

    [Fact]
    public void Parse_RealArm64DbxUpdate_ReadsAllHashes()
    {
        var list = Assert.Single(EfiSignatureDatabase.Parse(Fixtures.Read("DBXUpdate.arm64.bin")));

        Assert.Equal(26, list.Entries.Count);
    }

    [Fact]
    public void Parse_RealUpdateWithCertificateAndSvn_ReadsBothTypes()
    {
        var lists = EfiSignatureDatabase.Parse(Fixtures.Read("DBXUpdate2024.bin"));

        Assert.Equal([EfiSignatureType.X509, EfiSignatureType.Sha256], lists.Select(l => l.Type));
        var certificate = Assert.Single(lists[0].Entries);
        Assert.Equal("580a6f4cc4e4b669b9ebdc1b2b3e087b80d0678d", Convert.ToHexStringLower(SHA1.HashData(certificate.Data.Span)));
        Assert.Equal(3, lists[1].Entries.Count);
        Assert.All(lists[1].Entries, e => Assert.Equal(DbxBuilder.SvnOwner, e.Owner));
    }

    [Fact]
    public void Parse_WithoutTheAuthenticationHeader_GivesTheSameLists()
    {
        var signed = Fixtures.Read("DBXUpdate.arm64.bin");
        var certLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(16));

        var plain = EfiSignatureDatabase.Parse(signed.AsMemory(16 + certLength));

        Assert.Equal(26, Assert.Single(plain).Entries.Count);
    }

    [Fact]
    public void Parse_WithTheAttributeBytesOfAnEfivarfsFile_SkipsThem()
    {
        var list = DbxBuilder.Sha256List(new string('A', 64), new string('B', 64));

        byte[] withAttributes = [7, 0, 0, 0, .. list];

        var parsed = EfiSignatureDatabase.Parse(withAttributes);

        Assert.Equal(2, Assert.Single(parsed).Entries.Count);
    }

    [Fact]
    public void Parse_EmptyInput_IsAnEmptyDatabase()
    {
        Assert.Empty(EfiSignatureDatabase.Parse(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Parse_SeveralListsWithHeaders_AreReadInOrder()
    {
        byte[] data =
        [
            .. DbxBuilder.List(DbxBuilder.Sha256Type, [(DbxBuilder.SomeOwner, new byte[32])], header: [1, 2, 3, 4, 5]),
            .. DbxBuilder.List(DbxBuilder.X509Type, [(DbxBuilder.SomeOwner, new byte[200]), (DbxBuilder.SomeOwner, new byte[200])]),
            .. DbxBuilder.List(Guid.NewGuid(), [(DbxBuilder.SomeOwner, new byte[8])]),
        ];

        var lists = EfiSignatureDatabase.Parse(data);

        Assert.Equal([EfiSignatureType.Sha256, EfiSignatureType.X509, EfiSignatureType.Unknown], lists.Select(l => l.Type));
        Assert.Equal([1, 2, 3, 4, 5], lists[0].Header.ToArray());
        Assert.Equal(2, lists[1].Entries.Count);
        Assert.Equal(200, lists[1].Entries[1].Data.Length);
    }

    [Theory]
    [InlineData("c1c41626-504c-4092-aca9-41f936934328", EfiSignatureType.Sha256)]
    [InlineData("a5c059a1-94e4-4aa7-87b5-ab155c2bf072", EfiSignatureType.X509)]
    [InlineData("826ca512-cf10-4ac9-b187-be01496631bd", EfiSignatureType.Sha1)]
    [InlineData("0b6e5233-a65c-44c9-9407-d9ab83bfc8bd", EfiSignatureType.Sha224)]
    [InlineData("ff3e5307-9fd0-48c9-85f1-8ad56c701e01", EfiSignatureType.Sha384)]
    [InlineData("093e0fae-a6c4-4f50-9f1b-d41e2b89c19a", EfiSignatureType.Sha512)]
    [InlineData("3bd2a492-96c0-4079-b420-fcf98ef103ed", EfiSignatureType.X509Sha256)]
    [InlineData("7076876e-80c2-4ee6-aad2-28b349a6865b", EfiSignatureType.X509Sha384)]
    [InlineData("446dbf63-2502-4cda-bcfa-2465d2b0fe9d", EfiSignatureType.X509Sha512)]
    [InlineData("3c5766e8-269c-4e34-aa14-ed776e85b3b6", EfiSignatureType.Rsa2048)]
    [InlineData("4aafd29d-68df-49ee-8aa9-347d375665a7", EfiSignatureType.Pkcs7)]
    [InlineData("00000000-0000-0000-0000-000000000000", EfiSignatureType.Unknown)]
    public void GetType_MapsTheGuidsOfUefiSpecification32_4_1(string guidText, EfiSignatureType expected)
    {
        Assert.Equal(expected, EfiSignatureDatabase.GetType(new Guid(guidText)));
    }

    [Fact]
    public void Parse_TruncatedHeader_IsRejected()
    {
        AssertInvalid(new byte[27]);
    }

    [Fact]
    public void Parse_ListLongerThanTheData_IsRejected()
    {
        var list = DbxBuilder.Sha256List(new string('A', 64));

        AssertInvalid(list[..^1]);
    }

    [Theory]
    [InlineData(16, 0u)]
    [InlineData(16, 27u)]
    [InlineData(16, 0xFFFFFFFFu)]
    [InlineData(20, 0xFFFFFFFFu)]
    [InlineData(20, 9999u)]
    [InlineData(24, 0u)]
    [InlineData(24, 15u)]
    [InlineData(24, 47u)]
    [InlineData(24, 0xFFFFFFFFu)]
    public void Parse_InconsistentSizeFields_AreRejected(int offset, uint value)
    {
        var list = DbxBuilder.Sha256List(new string('A', 64), new string('B', 64));
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(offset), value);

        AssertInvalid(list);
    }

    [Fact]
    public void Parse_TrailingBytesAfterTheLastList_AreRejected()
    {
        byte[] data = [.. DbxBuilder.Sha256List(new string('A', 64)), 1, 2, 3];

        AssertInvalid(data);
    }

    [Fact]
    public void Parse_RandomlyDamagedRealFile_NeverThrowsAnythingButBootrixException()
    {
        var original = Fixtures.Read("DBXUpdate.arm64.bin");
        var random = new Random(31337);

        for (var round = 0; round < 3000; round++)
        {
            var copy = (byte[])original.Clone();
            for (var change = 0; change < 1 + random.Next(5); change++)
            {
                copy[random.Next(random.Next(3) == 0 ? copy.Length : 3400)] = (byte)random.Next(256);
            }

            try
            {
                EfiSignatureDatabase.Parse(copy);
            }
            catch (BootrixException ex)
            {
                Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
            }
        }
    }

    [Fact]
    public void Parse_EveryTruncationOfARealFile_FailsCleanlyOrParses()
    {
        var original = Fixtures.Read("DBXUpdateSVN.bin");

        for (var length = 0; length < original.Length; length++)
        {
            try
            {
                EfiSignatureDatabase.Parse(original.AsMemory(0, length));
            }
            catch (BootrixException ex)
            {
                Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
            }
        }
    }
}
