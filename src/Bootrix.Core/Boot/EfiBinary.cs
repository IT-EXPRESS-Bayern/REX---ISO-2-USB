// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot;

public sealed record EfiSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);

/// <summary>One entry of the PE certificate table (WIN_CERTIFICATE); <see cref="Data"/> excludes the 8-byte header.</summary>
public sealed record WinCertificate(ushort Revision, ushort Type, ReadOnlyMemory<byte> Data)
{
    public const ushort TypeX509 = 0x0001;
    public const ushort TypePkcsSignedData = 0x0002;
    public const ushort TypeEfiGuid = 0x0EF1;
}

/// <summary>
/// A PE/COFF image as used for UEFI applications. The file is parsed defensively because media can
/// contain truncated or hostile files; every structural problem surfaces as a <see cref="BootrixException"/>.
/// </summary>
public sealed class EfiBinary
{
    /// <summary>Default limit for a loaded file; Linux kernels with an EFI stub are the biggest legitimate case.</summary>
    public const int MaxFileSize = 128 * 1024 * 1024;

    private const int MaxCertificateTableSize = 4 * 1024 * 1024;
    private const int MaxCertificates = 32;
    private const int SecurityDirectoryIndex = 4;
    private const int HashChunkSize = 1024 * 1024;

    private readonly byte[] _data;
    private readonly PEHeaders _headers;
    private readonly int _checksumOffset;
    private readonly int _securityEntryOffset;
    private readonly int _headersSize;
    private readonly int _certificateOffset;
    private readonly int _certificateSize;

    private EfiBinary(
        byte[] data,
        PEHeaders headers,
        List<EfiSection> sections,
        List<WinCertificate> certificates,
        int checksumOffset,
        int securityEntryOffset,
        int certificateOffset,
        int certificateSize)
    {
        _data = data;
        _headers = headers;
        _checksumOffset = checksumOffset;
        _securityEntryOffset = securityEntryOffset;
        _certificateOffset = certificateOffset;
        _certificateSize = certificateSize;
        _headersSize = headers.PEHeader!.SizeOfHeaders;

        RawMachine = (ushort)headers.CoffHeader.Machine;
        Machine = EfiMachineInfo.FromPeMachine(RawMachine);
        Subsystem = headers.PEHeader.Subsystem;
        Sections = sections;
        Certificates = certificates;
    }

    public EfiMachine Machine { get; }

    public ushort RawMachine { get; }

    public Subsystem Subsystem { get; }

    public bool IsEfiImage => Subsystem is >= Subsystem.EfiApplication and <= Subsystem.EfiRom;

    public long Size => _data.Length;

    public IReadOnlyList<EfiSection> Sections { get; }

    /// <summary>Entries of the security directory; empty for an unsigned image.</summary>
    public IReadOnlyList<WinCertificate> Certificates { get; }

    public bool IsSigned => Certificates.Count > 0;

    /// <summary>Takes the array without copying; the caller must not modify it afterwards.</summary>
    public static EfiBinary Parse(byte[] data, int maxFileSize = MaxFileSize)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > maxFileSize)
        {
            throw TooLarge(maxFileSize);
        }

        PEHeaders headers;
        try
        {
            headers = new PEHeaders(new MemoryStream(data, writable: false));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Invalid(ex.Message, ex);
        }

        var pe = headers.PEHeader ?? throw Invalid("no optional header");
        if (pe.SizeOfHeaders <= 0 || pe.SizeOfHeaders > data.Length)
        {
            throw Invalid("SizeOfHeaders points outside of the file");
        }

        var sections = ReadSections(headers, data.Length);

        var optionalStart = headers.PEHeaderStartOffset;
        var checksumOffset = optionalStart + 64;
        var directoriesStart = optionalStart + (pe.Magic == PEMagic.PE32Plus ? 112 : 96);
        var hasSecurityEntry = pe.NumberOfRvaAndSizes > SecurityDirectoryIndex;
        var securityEntryOffset = hasSecurityEntry ? directoriesStart + SecurityDirectoryIndex * 8 : -1;

        var optionalEnd = (long)optionalStart + headers.CoffHeader.SizeOfOptionalHeader;
        if (checksumOffset + 4 > pe.SizeOfHeaders
            || (hasSecurityEntry && (securityEntryOffset + 8 > pe.SizeOfHeaders || securityEntryOffset + 8 > optionalEnd)))
        {
            throw Invalid("optional header does not fit into the headers");
        }

        var certificates = new List<WinCertificate>();
        int certificateOffset = 0, certificateSize = 0;
        if (hasSecurityEntry && pe.CertificateTableDirectory.Size != 0)
        {
            // Unlike other directories this one holds a file offset, not an RVA.
            certificateOffset = pe.CertificateTableDirectory.RelativeVirtualAddress;
            certificateSize = pe.CertificateTableDirectory.Size;
            if (certificateOffset < pe.SizeOfHeaders
                || certificateSize < 0
                || certificateSize > MaxCertificateTableSize
                || (long)certificateOffset + certificateSize > data.Length)
            {
                throw Invalid("certificate table lies outside of the file or is too large");
            }

            ReadCertificates(data, certificateOffset, certificateSize, certificates);
        }

        return new EfiBinary(data, headers, sections, certificates, checksumOffset, securityEntryOffset, certificateOffset, certificateSize);
    }

    /// <summary>Reads the rest of <paramref name="stream"/> and parses it; streams longer than the limit are rejected.</summary>
    public static EfiBinary Load(Stream stream, int maxFileSize = MaxFileSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
        {
            var length = stream.Length - stream.Position;
            if (length > maxFileSize)
            {
                throw TooLarge(maxFileSize);
            }

            var data = new byte[length];
            for (var done = 0; done < data.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(data, done, Math.Min(HashChunkSize, data.Length - done));
                if (read == 0)
                {
                    throw Invalid("stream ended before its announced length");
                }

                done += read;
            }

            return Parse(data, maxFileSize);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[HashChunkSize];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length + count > maxFileSize)
            {
                throw TooLarge(maxFileSize);
            }

            buffer.Write(chunk, 0, count);
        }

        return Parse(buffer.ToArray(), maxFileSize);
    }

    /// <summary>
    /// The Authenticode digest of the image (PE/COFF specification, "Calculating the PE Image Hash"):
    /// the checksum field, the security directory entry and the certificate table are left out, sections are
    /// hashed in file order and data after the last section is included.
    /// </summary>
    public byte[] ComputeAuthenticodeHash(HashAlgorithmName algorithm, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);

        Append(hash, 0, _checksumOffset, cancellationToken);
        var next = _checksumOffset + 4;
        if (_securityEntryOffset >= 0)
        {
            Append(hash, next, _securityEntryOffset, cancellationToken);
            next = _securityEntryOffset + 8;
        }

        Append(hash, next, _headersSize, cancellationToken);

        // The specification tracks SUM_OF_BYTES_HASHED as a running sum of section sizes; firmware does the same,
        // so the trailing-data start must be derived from the sum and not from the end of the last section.
        long hashed = _headersSize;
        foreach (var section in Sections.Where(s => s.RawSize != 0).OrderBy(s => s.RawOffset))
        {
            Append(hash, (int)section.RawOffset, (int)(section.RawOffset + section.RawSize), cancellationToken);
            hashed += section.RawSize;
        }

        if (hashed > _data.Length)
        {
            throw Invalid("sections overlap or exceed the file");
        }

        var tail = (int)hashed;
        if (_certificateSize == 0)
        {
            Append(hash, tail, _data.Length, cancellationToken);

            // A signer pads the file to a multiple of 8 before it appends the certificate table, and the digest covers
            // that padding. Only with it does the hash of an unsigned image equal the one pesign and osslsigncode sign.
            var padding = (8 - _data.Length % 8) % 8;
            if (padding > 0)
            {
                hash.AppendData(new byte[padding]);
            }
        }
        else
        {
            if (_certificateOffset < tail)
            {
                throw Invalid("certificate table overlaps image data");
            }

            Append(hash, tail, _certificateOffset, cancellationToken);
            Append(hash, _certificateOffset + _certificateSize, _data.Length, cancellationToken);
        }

        return hash.GetHashAndReset();
    }

    public bool TryGetSectionData(string name, out ReadOnlyMemory<byte> data)
    {
        foreach (var section in Sections)
        {
            if (!string.Equals(section.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var length = section.VirtualSize == 0 ? section.RawSize : Math.Min(section.RawSize, section.VirtualSize);
            data = _data.AsMemory((int)section.RawOffset, (int)length);
            return true;
        }

        data = default;
        return false;
    }

    /// <summary>The entries of the .sbat section, or null when the image has none.</summary>
    public IReadOnlyList<SbatEntry>? ReadSbat() =>
        TryGetSectionData(".sbat", out var data) ? SbatEntry.ParseSection(data.Span) : null;

    /// <summary>
    /// The Secure Version Number of a Windows boot manager. Microsoft stores it as RCDATA resource
    /// BOOTMGRSECURITYVERSIONNUMBER (the same lookup Windows tools use); images without it return null.
    /// </summary>
    public SecurityVersion? ReadBootmgrSecurityVersion()
    {
        var value = PeResources.FindRcData(_data, _headers, "BOOTMGRSECURITYVERSIONNUMBER");
        if (value is not { Length: >= 4 } bytes)
        {
            return null;
        }

        var span = bytes.Span;
        return new SecurityVersion(
            BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(span));
    }

    private static List<EfiSection> ReadSections(PEHeaders headers, int fileLength)
    {
        var sections = new List<EfiSection>(headers.SectionHeaders.Length);
        foreach (var header in headers.SectionHeaders)
        {
            if (header.PointerToRawData < 0 || header.SizeOfRawData < 0 || header.VirtualAddress < 0 || header.VirtualSize < 0)
            {
                throw Invalid("section " + header.Name + " has a negative size or offset");
            }

            if ((long)header.PointerToRawData + header.SizeOfRawData > fileLength)
            {
                throw Invalid("section " + header.Name + " extends beyond the end of the file");
            }

            sections.Add(new EfiSection(
                header.Name,
                (uint)header.VirtualAddress,
                (uint)header.VirtualSize,
                (uint)header.PointerToRawData,
                (uint)header.SizeOfRawData));
        }

        return sections;
    }

    private static void ReadCertificates(byte[] data, int offset, int size, List<WinCertificate> result)
    {
        var position = (long)offset;
        var end = (long)offset + size;
        while (end - position >= 8)
        {
            var header = data.AsSpan((int)position, 8);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length == 0 && data.AsSpan((int)position, (int)(end - position)).IndexOfAnyExcept((byte)0) < 0)
            {
                break; // zero padding up to the size announced by the directory
            }

            if (length < 8 || length > end - position)
            {
                throw Invalid("malformed certificate table");
            }

            if (result.Count >= MaxCertificates)
            {
                throw Invalid("too many certificates");
            }

            result.Add(new WinCertificate(
                BinaryPrimitives.ReadUInt16LittleEndian(header[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
                data.AsMemory((int)position + 8, (int)length - 8)));

            // Entries are padded to 8 bytes; the padding is not part of dwLength.
            position += (length + 7L) & ~7L;
        }
    }

    private void Append(IncrementalHash hash, int start, int end, CancellationToken cancellationToken)
    {
        if (start < 0 || end < start || end > _data.Length)
        {
            throw Invalid("hash range lies outside of the file");
        }

        for (var position = start; position < end; position += HashChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(_data, position, Math.Min(HashChunkSize, end - position));
        }
    }

    private static BootrixException TooLarge(int limit) =>
        Invalid("file is larger than " + limit.ToString(CultureInfo.InvariantCulture) + " bytes");

    private static BootrixException Invalid(string reason, Exception? inner = null) =>
        new(ErrorCode.EfiBinaryInvalid, reason, inner) { Arguments = [reason] };
}
