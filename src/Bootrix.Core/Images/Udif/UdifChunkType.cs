// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Udif;

/// <summary>Chunk types of a UDIF block table. Values with the top bit set are compressed.</summary>
public enum UdifChunkType : uint
{
    ZeroFill = 0x00000000,
    Raw = 0x00000001,

    /// <summary>Free space; carries no data and reads as zeros.</summary>
    Ignore = 0x00000002,
    Comment = 0x7FFFFFFE,
    Adc = 0x80000004,
    Zlib = 0x80000005,
    Bzip2 = 0x80000006,
    Lzfse = 0x80000007,

    /// <summary>The XZ container (LZMA2), not a bare LZMA stream.</summary>
    Xz = 0x80000008,
    Terminator = 0xFFFFFFFF,
}
