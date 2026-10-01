// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Workshop.Licensing;

namespace Bootrix.Core.Workshop.Firmware;

public sealed record MsdmTable
{
    public required string OemId { get; init; }

    public required string OemTableId { get; init; }

    public bool ChecksumValid { get; init; }

    /// <summary>The key in clear text; null when the table carries none or the data is not a well-formed key.</summary>
    [Sensitive]
    public string? ProductKey { get; init; }

    public string? MaskedKey => ProductKeys.Mask(ProductKey);

    // The generated ToString would print the key; list everything else.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("OemId = ").Append(OemId)
            .Append(", OemTableId = ").Append(OemTableId)
            .Append(", ChecksumValid = ").Append(ChecksumValid)
            .Append(", MaskedKey = ").Append(MaskedKey);
        return true;
    }
}

/// <summary>
/// Reads the ACPI MSDM (Microsoft Data Management) table that OEMs use to embed the Windows key in the firmware. After the
/// 36-byte ACPI header come a version and a reserved dword, then a descriptor of data type, reserved and length dwords, and
/// finally the key as 29 ASCII characters at offset 56.
/// </summary>
public static class MsdmParser
{
    public const int KeyOffset = 56;
    public const int KeyLength = ProductKeys.FormattedLength;

    private const int LengthOffset = 4;
    private const int OemIdOffset = 10;
    private const int OemTableIdOffset = 16;
    private const int DataTypeOffset = 44;
    private const int DataLengthOffset = 52;
    private const uint DataTypeProductKey = 1;

    public static MsdmTable? Parse(ReadOnlySpan<byte> table)
    {
        if (table.Length < KeyOffset || !table[..4].SequenceEqual("MSDM"u8))
        {
            return null;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(table[LengthOffset..]);
        if (declared < KeyOffset || declared > table.Length)
        {
            return null;
        }

        var content = table[..(int)declared];
        var checksum = 0;
        foreach (var b in content)
        {
            checksum += b;
        }

        return new MsdmTable
        {
            OemId = Ascii(content.Slice(OemIdOffset, 6)),
            OemTableId = Ascii(content.Slice(OemTableIdOffset, 8)),
            ChecksumValid = (checksum & 0xFF) == 0,
            ProductKey = ReadKey(content),
        };
    }

    private static string? ReadKey(ReadOnlySpan<byte> content)
    {
        var dataType = BinaryPrimitives.ReadUInt32LittleEndian(content[DataTypeOffset..]);
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(content[DataLengthOffset..]);
        if (dataType != DataTypeProductKey || dataLength == 0)
        {
            return null;
        }

        var length = (int)Math.Min(dataLength, content.Length - KeyOffset);
        var key = Ascii(content.Slice(KeyOffset, length)).ToUpperInvariant();
        return ProductKeys.IsValidFormat(key) ? key : null;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
}
