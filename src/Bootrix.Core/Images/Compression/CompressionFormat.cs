// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Compression;

public enum CompressionFormat
{
    None,
    GZip,
    BZip2,
    Xz,
    Zstd,

    /// <summary>Raw LZMA ("LZMA alone", .lzma); has no magic number and is recognised by its header fields.</summary>
    Lzma,

    /// <summary>Unix compress (.Z, LZW).</summary>
    Compress,
    Zip,
}
