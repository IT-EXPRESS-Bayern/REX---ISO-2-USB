// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Bootrix.Core.Workshop.Firmware;

/// <summary>
/// Reads the SMBIOS structure table (DMTF DSP0134). Every structure is a formatted area of fixed offsets followed by a set
/// of NUL-terminated strings that the formatted fields refer to by one-based index; the set ends with an extra NUL.
/// </summary>
public static class SmbiosParser
{
    private const int RawHeaderLength = 8;
    private const int StructureHeaderLength = 4;
    private const byte TypeBios = 0;
    private const byte TypeSystem = 1;
    private const byte TypeBaseBoard = 2;
    private const byte TypeChassis = 3;
    private const byte TypeEndOfTable = 127;

    private static readonly string[] DateFormats = ["MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy", "yyyy-MM-dd"];

    /// <summary>Text boards ship with when the manufacturer did not fill the field in; it carries no information about the machine.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.",
        "To be filled by O.E.M.",
        "Default string",
        "Not Specified",
        "Not Applicable",
        "None",
        "N/A",
        "Unknown",
        "O.E.M.",
        "OEM",
        "System Product Name",
        "System manufacturer",
        "System Version",
        "System Serial Number",
        "123456789",
        "1234567890",
    };

    /// <summary>Parses the buffer returned by GetSystemFirmwareTable('RSMB'): an eight-byte RawSMBIOSData header followed by the table.</summary>
    public static SmbiosData? ParseRawFirmwareTable(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < RawHeaderLength)
        {
            return null;
        }

        var major = raw[1];
        var minor = raw[2];
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        var available = raw.Length - RawHeaderLength;
        var length = declared > (uint)available ? available : (int)declared;

        return ParseStructureTable(raw.Slice(RawHeaderLength, length), major, minor);
    }

    public static SmbiosData ParseStructureTable(ReadOnlySpan<byte> table, int majorVersion, int minorVersion)
    {
        var data = new SmbiosData { MajorVersion = majorVersion, MinorVersion = minorVersion };
        var seen = TypeSet.None;
        var offset = 0;

        while (offset + StructureHeaderLength <= table.Length)
        {
            var type = table[offset];
            var length = table[offset + 1];

            // A formatted area shorter than its own header, or one that runs past the table, means the table is damaged.
            if (length < StructureHeaderLength || offset + length > table.Length)
            {
                break;
            }

            var formatted = table.Slice(offset, length);
            var strings = new List<string>();
            offset = ReadStringSet(table, offset + length, strings);

            if (type == TypeEndOfTable)
            {
                break;
            }

            // Boards and chassis can appear more than once; the first one describes the machine itself.
            var flag = FlagOf(type);
            if (flag == TypeSet.None || seen.HasFlag(flag))
            {
                continue;
            }

            seen |= flag;
            data = type switch
            {
                TypeBios => ReadBios(data, formatted, strings),
                TypeSystem => ReadSystem(data, formatted, strings),
                TypeBaseBoard => ReadBaseBoard(data, formatted, strings),
                _ => ReadChassis(data, formatted),
            };
        }

        return data;
    }

    private static SmbiosData ReadBios(SmbiosData data, ReadOnlySpan<byte> f, List<string> strings) => data with
    {
        BiosVendor = Text(f, 0x04, strings),
        BiosVersion = Text(f, 0x05, strings),
        BiosReleaseDate = ParseDate(Text(f, 0x08, strings)),
    };

    private static SmbiosData ReadSystem(SmbiosData data, ReadOnlySpan<byte> f, List<string> strings) => data with
    {
        SystemManufacturer = Text(f, 0x04, strings),
        SystemProductName = Text(f, 0x05, strings),
        SystemVersion = Text(f, 0x06, strings),
        SystemSerialNumber = Text(f, 0x07, strings),
        SystemUuid = ReadUuid(f, data.MajorVersion, data.MinorVersion),
        SystemSku = Text(f, 0x19, strings),
        SystemFamily = Text(f, 0x1A, strings),
    };

    private static SmbiosData ReadBaseBoard(SmbiosData data, ReadOnlySpan<byte> f, List<string> strings) => data with
    {
        BoardManufacturer = Text(f, 0x04, strings),
        BoardProduct = Text(f, 0x05, strings),
        BoardVersion = Text(f, 0x06, strings),
        BoardSerialNumber = Text(f, 0x07, strings),
    };

    private static SmbiosData ReadChassis(SmbiosData data, ReadOnlySpan<byte> f) => data with
    {
        // Bit 7 of the chassis type byte only says whether a lock is present.
        ChassisType = f.Length > 0x05 ? (byte)(f[0x05] & 0x7F) : null,
    };

    private static Guid? ReadUuid(ReadOnlySpan<byte> f, int major, int minor)
    {
        const int offset = 0x08;
        if (f.Length < offset + 16)
        {
            return null;
        }

        var bytes = f.Slice(offset, 16);
        if (!bytes.ContainsAnyExcept((byte)0x00) || !bytes.ContainsAnyExcept((byte)0xFF))
        {
            // All zero means "not set", all 0xFF means "not settable".
            return null;
        }

        // From SMBIOS 2.6 the first three fields are little-endian, as in Windows' own GUID layout; before that the bytes are in order.
        var littleEndian = major > 2 || (major == 2 && minor >= 6);
        return new Guid(bytes, bigEndian: !littleEndian);
    }

    private static string? Text(ReadOnlySpan<byte> formatted, int offset, List<string> strings)
    {
        if (formatted.Length <= offset)
        {
            return null;
        }

        var index = formatted[offset];
        if (index == 0 || index > strings.Count)
        {
            return null;
        }

        var text = strings[index - 1].Trim();
        return text.Length == 0 || Placeholders.Contains(text) ? null : text;
    }

    private static DateOnly? ParseDate(string? text) =>
        text is not null && DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>Reads the string set after a formatted area and returns the offset of the next structure.</summary>
    private static int ReadStringSet(ReadOnlySpan<byte> table, int start, List<string> strings)
    {
        // Without strings the set is just two NULs.
        if (start + 1 < table.Length && table[start] == 0 && table[start + 1] == 0)
        {
            return start + 2;
        }

        var position = start;
        while (position < table.Length)
        {
            var remaining = table[position..];
            var end = remaining.IndexOf((byte)0);
            if (end < 0)
            {
                strings.Add(Encoding.UTF8.GetString(remaining));
                return table.Length;
            }

            strings.Add(Encoding.UTF8.GetString(remaining[..end]));
            position += end + 1;
            if (position < table.Length && table[position] == 0)
            {
                return position + 1;
            }
        }

        return table.Length;
    }

    private static TypeSet FlagOf(byte type) => type switch
    {
        TypeBios => TypeSet.Bios,
        TypeSystem => TypeSet.System,
        TypeBaseBoard => TypeSet.BaseBoard,
        TypeChassis => TypeSet.Chassis,
        _ => TypeSet.None,
    };

    [Flags]
    private enum TypeSet
    {
        None = 0,
        Bios = 1,
        System = 2,
        BaseBoard = 4,
        Chassis = 8,
    }
}
