// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot;

public enum EfiSignatureType
{
    Unknown,
    Sha256,
    X509,
    Sha1,
    Sha224,
    Sha384,
    Sha512,
    X509Sha256,
    X509Sha384,
    X509Sha512,
    Rsa2048,
    Pkcs7,
}

/// <summary>EFI_SIGNATURE_DATA: the owner GUID and the type specific payload (a hash, a DER certificate, ...).</summary>
public readonly record struct EfiSignatureEntry(Guid Owner, ReadOnlyMemory<byte> Data);

public sealed record EfiSignatureList(
    EfiSignatureType Type,
    Guid TypeGuid,
    ReadOnlyMemory<byte> Header,
    IReadOnlyList<EfiSignatureEntry> Entries);

/// <summary>
/// Reader for the content of the UEFI signature databases (db, dbx, ...): a sequence of EFI_SIGNATURE_LIST
/// structures, optionally wrapped in the EFI_VARIABLE_AUTHENTICATION_2 header of a signed update file
/// or preceded by the four attribute bytes of a Linux efivarfs dump.
/// </summary>
public static class EfiSignatureDatabase
{
    private const int ListHeaderSize = 28; // GUID + three UINT32
    private const int OwnerSize = 16;
    private const int AuthenticationPrefixSize = 16 + 8 + 16; // EFI_TIME + WIN_CERTIFICATE header + CertType GUID
    private const ushort WinCertTypeEfiGuid = 0x0EF1;

    private static readonly (Guid Guid, EfiSignatureType Type)[] KnownTypes =
    [
        (new("c1c41626-504c-4092-aca9-41f936934328"), EfiSignatureType.Sha256),
        (new("a5c059a1-94e4-4aa7-87b5-ab155c2bf072"), EfiSignatureType.X509),
        (new("826ca512-cf10-4ac9-b187-be01496631bd"), EfiSignatureType.Sha1),
        (new("0b6e5233-a65c-44c9-9407-d9ab83bfc8bd"), EfiSignatureType.Sha224),
        (new("ff3e5307-9fd0-48c9-85f1-8ad56c701e01"), EfiSignatureType.Sha384),
        (new("093e0fae-a6c4-4f50-9f1b-d41e2b89c19a"), EfiSignatureType.Sha512),
        (new("3bd2a492-96c0-4079-b420-fcf98ef103ed"), EfiSignatureType.X509Sha256),
        (new("7076876e-80c2-4ee6-aad2-28b349a6865b"), EfiSignatureType.X509Sha384),
        (new("446dbf63-2502-4cda-bcfa-2465d2b0fe9d"), EfiSignatureType.X509Sha512),
        (new("3c5766e8-269c-4e34-aa14-ed776e85b3b6"), EfiSignatureType.Rsa2048),
        (new("4aafd29d-68df-49ee-8aa9-347d375665a7"), EfiSignatureType.Pkcs7),
    ];

    public static EfiSignatureType GetType(Guid typeGuid)
    {
        foreach (var (guid, type) in KnownTypes)
        {
            if (guid == typeGuid)
            {
                return type;
            }
        }

        return EfiSignatureType.Unknown;
    }

    public static IReadOnlyList<EfiSignatureList> Parse(ReadOnlyMemory<byte> data)
    {
        var body = StripAuthenticationHeader(data);
        BootrixException first;
        try
        {
            return ParseLists(body);
        }
        catch (BootrixException ex)
        {
            first = ex;
        }

        if (body.Length > 4)
        {
            try
            {
                return ParseLists(body[4..]);
            }
            catch (BootrixException)
            {
                // The error of the plain interpretation is the more useful one to report.
            }
        }

        throw first;
    }

    private static ReadOnlyMemory<byte> StripAuthenticationHeader(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < AuthenticationPrefixSize)
        {
            return data;
        }

        var certLength = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var certType = BinaryPrimitives.ReadUInt16LittleEndian(span[22..]);
        if (certType != WinCertTypeEfiGuid || certLength < 8 + 16 || 16L + certLength > span.Length)
        {
            return data;
        }

        return data[(16 + (int)certLength)..];
    }

    private static List<EfiSignatureList> ParseLists(ReadOnlyMemory<byte> data)
    {
        var lists = new List<EfiSignatureList>();
        var position = 0;
        while (position < data.Length)
        {
            var span = data.Span[position..];
            if (span.Length < ListHeaderSize)
            {
                throw Invalid("truncated signature list header");
            }

            var typeGuid = new Guid(span[..16]);
            var listSize = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
            var signatureSize = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);

            if (listSize < ListHeaderSize || listSize > span.Length || headerSize > listSize - ListHeaderSize)
            {
                throw Invalid("signature list size is inconsistent");
            }

            var payload = listSize - ListHeaderSize - headerSize;
            if (signatureSize < OwnerSize || payload % signatureSize != 0)
            {
                throw Invalid("signature size does not divide the list");
            }

            var entries = new List<EfiSignatureEntry>((int)(payload / signatureSize));
            var entryStart = position + ListHeaderSize + (int)headerSize;
            for (var i = 0; i < payload / signatureSize; i++)
            {
                var at = entryStart + (int)(i * signatureSize);
                entries.Add(new EfiSignatureEntry(
                    new Guid(data.Span.Slice(at, OwnerSize)),
                    data.Slice(at + OwnerSize, (int)signatureSize - OwnerSize)));
            }

            lists.Add(new EfiSignatureList(GetType(typeGuid), typeGuid, data.Slice(position + ListHeaderSize, (int)headerSize), entries));
            position += (int)listSize;
        }

        return lists;
    }

    private static BootrixException Invalid(string reason) =>
        new(ErrorCode.RevocationDataInvalid, reason) { Arguments = [reason] };
}
