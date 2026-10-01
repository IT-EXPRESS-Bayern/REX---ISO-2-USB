// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Workshop.Firmware;

namespace Bootrix.Core.Tests.Workshop;

public class MsdmParserTests
{
    private const string RealisticKey = "BCDFG-HJKMP-QRTVW-XY234-6789N";

    /// <summary>
    /// The MSDM table as Microsoft specifies it: 36-byte ACPI header, version and reserved dwords, then the software licensing
    /// data descriptor (type, reserved, length) and the key, which puts the key at offset 56.
    /// </summary>
    private static byte[] Table(string key, uint dataType = 1, uint? dataLength = null, string oemId = "DELL  ", string oemTableId = "CBX3    ")
    {
        var data = Encoding.ASCII.GetBytes(key);
        var table = new byte[56 + data.Length];
        Encoding.ASCII.GetBytes("MSDM").CopyTo(table, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), (uint)table.Length);
        table[8] = 3;
        Encoding.ASCII.GetBytes(oemId).CopyTo(table, 10);
        Encoding.ASCII.GetBytes(oemTableId).CopyTo(table, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(44), dataType);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(52), dataLength ?? (uint)data.Length);
        data.CopyTo(table, 56);

        var sum = 0;
        foreach (var b in table)
        {
            sum += b;
        }

        table[9] = (byte)(-sum & 0xFF);
        return table;
    }

    [Fact]
    public void Parse_ReadsTheKeyAtOffset56()
    {
        var table = Table(RealisticKey);

        var msdm = MsdmParser.Parse(table);

        Assert.NotNull(msdm);
        Assert.Equal(RealisticKey, msdm.ProductKey);
        Assert.Equal(56, MsdmParser.KeyOffset);
        Assert.Equal(29, MsdmParser.KeyLength);
        Assert.Equal(RealisticKey, Encoding.ASCII.GetString(table, 56, 29));
    }

    [Fact]
    public void Parse_ReadsOemIdsAndChecksum()
    {
        var msdm = MsdmParser.Parse(Table(RealisticKey, oemId: "LENOVO", oemTableId: "TP-R0F  "));

        Assert.Equal("LENOVO", msdm!.OemId);
        Assert.Equal("TP-R0F", msdm.OemTableId);
        Assert.True(msdm.ChecksumValid);
    }

    [Fact]
    public void Parse_BrokenChecksum_IsFlaggedButStillRead()
    {
        var table = Table(RealisticKey);
        table[9] ^= 0x5A;

        var msdm = MsdmParser.Parse(table);

        Assert.False(msdm!.ChecksumValid);
        Assert.Equal(RealisticKey, msdm.ProductKey);
    }

    [Fact]
    public void Parse_TrailingBytesBeyondTheDeclaredLength_AreIgnored()
    {
        var table = Table(RealisticKey).Concat(new byte[] { 0x41, 0x41, 0x41 }).ToArray();

        Assert.Equal(RealisticKey, MsdmParser.Parse(table)!.ProductKey);
    }

    [Fact]
    public void Parse_WrongSignature_ReturnsNull()
    {
        var table = Table(RealisticKey);
        table[0] = (byte)'X';

        Assert.Null(MsdmParser.Parse(table));
    }

    [Fact]
    public void Parse_DeclaredLengthLargerThanBuffer_ReturnsNull()
    {
        var table = Table(RealisticKey);

        Assert.Null(MsdmParser.Parse(table.AsSpan(0, table.Length - 5)));
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        Assert.Null(MsdmParser.Parse(new byte[40]));
    }

    [Theory]
    [InlineData("00000-00000-00000-00000-00000")]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-678")]
    [InlineData("BCDFGHJKMPQRTVWXY2346789N1234")]
    [InlineData("bcdfg-hjkmp-qrtvw-xy234-6789n")]
    public void Parse_MalformedKey_YieldsNoKeyButKeepsTheTable(string key)
    {
        var msdm = MsdmParser.Parse(Table(key));

        Assert.NotNull(msdm);
        // Lower case is the only shape the parser repairs, because OEM tables are sometimes written that way.
        Assert.Equal(key.Equals("bcdfg-hjkmp-qrtvw-xy234-6789n", StringComparison.Ordinal) ? RealisticKey : null, msdm.ProductKey);
    }

    [Fact]
    public void Parse_DataTypeOtherThanProductKey_YieldsNoKey()
    {
        Assert.Null(MsdmParser.Parse(Table(RealisticKey, dataType: 2))!.ProductKey);
    }

    [Fact]
    public void Parse_ZeroDataLength_YieldsNoKey()
    {
        Assert.Null(MsdmParser.Parse(Table(RealisticKey, dataLength: 0))!.ProductKey);
    }

    [Fact]
    public void Parse_DataLengthBeyondTheTable_DoesNotReadOutside()
    {
        var msdm = MsdmParser.Parse(Table(RealisticKey, dataLength: 4000));

        Assert.Equal(RealisticKey, msdm!.ProductKey);
    }

    [Fact]
    public void ToString_NeverContainsTheKey()
    {
        var msdm = MsdmParser.Parse(Table(RealisticKey))!;

        var text = msdm.ToString();

        Assert.DoesNotContain("BCDFG", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HJKMP", text, StringComparison.Ordinal);
        Assert.Contains("XXXXX-XXXXX-XXXXX-XXXXX-6789N", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskedKey_HidesAllButTheLastGroup()
    {
        var msdm = MsdmParser.Parse(Table(RealisticKey))!;

        Assert.Equal("XXXXX-XXXXX-XXXXX-XXXXX-6789N", msdm.MaskedKey);
    }

    [Fact]
    public void ContainsCertificate_FindsTheSubjectNameInsideDerData()
    {
        // Subject names sit unencoded as ASCII inside the DER certificate that fills an EFI_SIGNATURE_LIST entry.
        var variable = new byte[200];
        Encoding.ASCII.GetBytes("CN=Windows UEFI CA 2023").CopyTo(variable, 120);

        Assert.True(SecureBootDatabase.ContainsCertificate(variable, SecureBootDatabase.WindowsUefiCa2023));
        Assert.False(SecureBootDatabase.ContainsCertificate(variable, SecureBootDatabase.WindowsProductionPca2011));
    }

    [Fact]
    public void ContainsCertificate_EmptyVariable_IsFalse()
    {
        Assert.False(SecureBootDatabase.ContainsCertificate([], SecureBootDatabase.WindowsUefiCa2023));
    }
}
