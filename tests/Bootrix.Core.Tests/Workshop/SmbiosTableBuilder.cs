// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Tests.Workshop;

/// <summary>Builds SMBIOS structure tables byte by byte, with the field offsets of DSP0134 written out.</summary>
internal sealed class SmbiosTableBuilder
{
    private readonly List<byte> _table = [];
    private ushort _handle = 0x100;

    public SmbiosTableBuilder AddBios(string vendor, string version, string releaseDate)
    {
        var body = Structure(0, 0x18);
        body[0x04] = 1;
        body[0x05] = 2;
        body[0x08] = 3;
        return Append(body, vendor, version, releaseDate);
    }

    public SmbiosTableBuilder AddSystem(string manufacturer, string product, string version, string serial, byte[]? uuid = null, string? sku = null, string? family = null)
    {
        var body = Structure(1, 0x1B);
        body[0x04] = 1;
        body[0x05] = 2;
        body[0x06] = 3;
        body[0x07] = 4;
        uuid?.CopyTo(body, 0x08);
        var strings = new List<string> { manufacturer, product, version, serial };
        if (sku is not null)
        {
            strings.Add(sku);
            body[0x19] = (byte)strings.Count;
        }

        if (family is not null)
        {
            strings.Add(family);
            body[0x1A] = (byte)strings.Count;
        }

        return Append(body, [.. strings]);
    }

    public SmbiosTableBuilder AddBaseBoard(string manufacturer, string product, string version, string serial)
    {
        var body = Structure(2, 0x0F);
        body[0x04] = 1;
        body[0x05] = 2;
        body[0x06] = 3;
        body[0x07] = 4;
        return Append(body, manufacturer, product, version, serial);
    }

    public SmbiosTableBuilder AddChassis(byte typeByte)
    {
        var body = Structure(3, 0x16);
        body[0x05] = typeByte;
        return Append(body);
    }

    /// <summary>A structure type the parser does not read, to prove it skips over strings correctly.</summary>
    public SmbiosTableBuilder AddOther(byte type, params string[] strings) => Append(Structure(type, 0x08), strings);

    public SmbiosTableBuilder AddEndOfTable() => Append(Structure(127, 4));

    public byte[] Build() => [.. _table];

    /// <summary>The buffer GetSystemFirmwareTable('RSMB') returns: Used20CallingMethod, major, minor, DMI revision, length, table.</summary>
    public byte[] BuildRaw(byte major, byte minor)
    {
        var table = Build();
        var raw = new byte[8 + table.Length];
        raw[1] = major;
        raw[2] = minor;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)table.Length);
        table.CopyTo(raw, 8);
        return raw;
    }

    /// <summary>A dump file in the layout dmidecode reads with --from-dump: a 32-byte SMBIOS 2.x entry point followed by the table.</summary>
    public byte[] BuildDmidecodeDump(byte major, byte minor)
    {
        var table = Build();
        var entry = new byte[0x1F];
        Encoding.ASCII.GetBytes("_SM_").CopyTo(entry, 0);
        entry[0x05] = 0x1F;
        entry[0x06] = major;
        entry[0x07] = minor;
        Encoding.ASCII.GetBytes("_DMI_").CopyTo(entry, 0x10);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(0x16), (ushort)table.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0x18), 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(0x1C), 8);
        entry[0x15] = Checksum(entry.AsSpan(0x10, 0x0F));
        entry[0x04] = Checksum(entry);

        var dump = new byte[0x20 + table.Length];
        entry.CopyTo(dump, 0);
        table.CopyTo(dump, 0x20);
        return dump;
    }

    private byte[] Structure(byte type, int length)
    {
        var body = new byte[length];
        body[0] = type;
        body[1] = (byte)length;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), _handle++);
        return body;
    }

    private SmbiosTableBuilder Append(byte[] formatted, params string[] strings)
    {
        _table.AddRange(formatted);
        if (strings.Length == 0)
        {
            _table.AddRange([0, 0]);
            return this;
        }

        foreach (var text in strings)
        {
            _table.AddRange(Encoding.UTF8.GetBytes(text));
            _table.Add(0);
        }

        _table.Add(0);
        return this;
    }

    /// <summary>Byte that makes the sum of the range zero once it is stored in the range.</summary>
    private static byte Checksum(ReadOnlySpan<byte> range)
    {
        var sum = 0;
        foreach (var b in range)
        {
            sum += b;
        }

        return (byte)(-sum & 0xFF);
    }
}
