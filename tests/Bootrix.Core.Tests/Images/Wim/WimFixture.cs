// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Tests.Images.Wim;

/// <summary>Writes the smallest WIM that carries a header and an XML resource, so the parser can be tested without wimlib.</summary>
internal static class WimFixture
{
    public static string Xml(params string[] images) =>
        "<WIM>" + string.Concat(images) + "<TOTALBYTES>1000</TOTALBYTES></WIM>";

    public static string Image(int index, string name, int arch, int build, string edition = "Professional", string languages = "en-US", string extra = "") =>
        $"<IMAGE INDEX=\"{index}\"><DIRCOUNT>3</DIRCOUNT><FILECOUNT>9</FILECOUNT><TOTALBYTES>123456</TOTALBYTES>"
        + "<CREATIONTIME><HIGHPART>0x01DD516B</HIGHPART><LOWPART>0xF82C261E</LOWPART></CREATIONTIME>"
        + $"<WINDOWS><ARCH>{arch}</ARCH><PRODUCTNAME>Microsoft® Windows® Operating System</PRODUCTNAME><EDITIONID>{edition}</EDITIONID>"
        + $"<INSTALLATIONTYPE>Client</INSTALLATIONTYPE><LANGUAGES><LANGUAGE>{languages}</LANGUAGE><DEFAULT>{languages}</DEFAULT></LANGUAGES>"
        + $"<VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>{build}</BUILD><SPBUILD>1</SPBUILD><SPLEVEL>0</SPLEVEL></VERSION></WINDOWS>"
        + $"<NAME>{name}</NAME><DESCRIPTION>{name} description</DESCRIPTION><FLAGS>{edition}</FLAGS>{extra}</IMAGE>";

    /// <summary>Builds a file of 208 header bytes followed by the XML in UTF-16LE with a byte order mark.</summary>
    public static byte[] Build(string xml, uint flags = 0x2, uint version = 0x10D00, byte xmlResourceFlags = 0x2, bool includeXml = true, int parts = 1, int part = 1)
    {
        var xmlBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        var file = new byte[WimHeaderSize + (includeXml ? xmlBytes.Length : 0)];
        "MSWIM\0\0\0"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), WimHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), version);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x14), 32768);
        Guid.Parse("11112222-3333-4444-5555-666677778888").TryWriteBytes(file.AsSpan(0x18));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x28), (ushort)part);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x2A), (ushort)parts);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x2C), 1);
        if (includeXml)
        {
            WriteResource(file.AsSpan(0x48), xmlBytes.Length, xmlResourceFlags, WimHeaderSize, xmlBytes.Length);
            xmlBytes.CopyTo(file.AsSpan(WimHeaderSize));
        }

        return file;
    }

    public static void WriteResource(Span<byte> target, long stored, byte flags, long offset, long original)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(target, (ulong)stored | ((ulong)flags << 56));
        BinaryPrimitives.WriteUInt64LittleEndian(target[8..], (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(target[16..], (ulong)original);
    }

    private const int WimHeaderSize = 208;
}
