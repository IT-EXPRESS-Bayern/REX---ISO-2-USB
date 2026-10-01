// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Tests.Boot;

/// <summary>Writes EFI_SIGNATURE_LISTs for tests; the lists are constructed here, they are not real DBX content.</summary>
internal static class DbxBuilder
{
    public static readonly Guid Sha256Type = new("c1c41626-504c-4092-aca9-41f936934328");
    public static readonly Guid X509Type = new("a5c059a1-94e4-4aa7-87b5-ab155c2bf072");
    public static readonly Guid X509Sha256Type = new("3bd2a492-96c0-4079-b420-fcf98ef103ed");
    public static readonly Guid SvnOwner = new("9d132b6c-59d5-4388-ab1c-185cfcb2eb92");
    public static readonly Guid BootmgrSvn = new("9d132b61-59d5-4388-ab1c-185c3cb2eb92");
    public static readonly Guid SomeOwner = new("77fa9abd-0359-4d32-bd60-28f4e78f784b");

    public static byte[] List(Guid type, IEnumerable<(Guid Owner, byte[] Data)> entries, byte[]? header = null)
    {
        var items = entries.ToList();
        var signatureSize = 16 + (items.Count > 0 ? items.Max(i => i.Data.Length) : 0);
        header ??= [];
        using var stream = new MemoryStream();
        stream.Write(type.ToByteArray());
        WriteUInt32(stream, (uint)(28 + header.Length + items.Count * signatureSize));
        WriteUInt32(stream, (uint)header.Length);
        WriteUInt32(stream, (uint)signatureSize);
        stream.Write(header);
        foreach (var (owner, data) in items)
        {
            stream.Write(owner.ToByteArray());
            stream.Write(data);
            stream.Write(new byte[signatureSize - 16 - data.Length]);
        }

        return stream.ToArray();
    }

    public static byte[] Sha256List(params string[] hashes) =>
        List(Sha256Type, hashes.Select(h => (SomeOwner, Convert.FromHexString(h))));

    /// <summary>SVN_DATA: version 1, application GUID, minor and major as UINT16, zero padding to 32 bytes.</summary>
    public static byte[] SvnData(Guid component, ushort major, ushort minor)
    {
        var data = new byte[32];
        data[0] = 1;
        component.ToByteArray().CopyTo(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(17), minor);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(19), major);
        return data;
    }

    /// <summary>Wraps lists in the header of an authenticated variable update (EFI_TIME + WIN_CERTIFICATE_UEFI_GUID).</summary>
    public static byte[] Authenticated(byte[] lists, int certificateDataLength = 100)
    {
        var certLength = 8 + 16 + certificateDataLength;
        var header = new byte[16 + certLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)certLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 0x0EF1);
        new Guid("4aafd29d-68df-49ee-8aa9-347d375665a7").ToByteArray().CopyTo(header, 24);
        return [.. header, .. lists];
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
