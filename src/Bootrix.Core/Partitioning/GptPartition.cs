// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Partitioning;

/// <summary>One 128-byte GPT partition entry; both LBAs are inclusive.</summary>
public sealed record GptPartition(
    Guid TypeGuid,
    Guid UniqueGuid,
    long FirstLba,
    long LastLba,
    GptAttributes Attributes = GptAttributes.None,
    string Name = "")
{
    public const int EntrySize = 128;
    public const int MaxNameLength = 36;

    public long SectorCount => LastLba - FirstLba + 1;

    internal static GptPartition? Read(ReadOnlySpan<byte> entry)
    {
        var type = new Guid(entry[..16]);
        if (type == Guid.Empty)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(entry.Slice(56, MaxNameLength * 2));
        var end = name.IndexOf('\0', StringComparison.Ordinal);
        return new GptPartition(
            type,
            new Guid(entry[16..32]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[40..]),
            (GptAttributes)BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]),
            end < 0 ? name : name[..end]);
    }

    internal void Write(Span<byte> entry)
    {
        TypeGuid.TryWriteBytes(entry[..16]);
        UniqueGuid.TryWriteBytes(entry[16..32]);
        BinaryPrimitives.WriteInt64LittleEndian(entry[32..], FirstLba);
        BinaryPrimitives.WriteInt64LittleEndian(entry[40..], LastLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[48..], (ulong)Attributes);
        Encoding.Unicode.GetBytes(Name, entry.Slice(56, MaxNameLength * 2));
    }
}
