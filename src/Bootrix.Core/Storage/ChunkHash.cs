// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Hashing;

namespace Bootrix.Core.Storage;

/// <summary>Fast non-cryptographic hash used to compare what was written with what is read back.</summary>
internal static class ChunkHash
{
    public static ulong Compute(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);
}

internal readonly record struct ChunkRecord(long Offset, int Length, ulong Hash);
