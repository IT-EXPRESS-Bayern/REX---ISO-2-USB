// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.IO;

namespace Bootrix.Core.Partitioning;

/// <summary>Assembles a valid MBR: boot code, signature and up to four non-overlapping partitions with CHS values filled in.</summary>
public sealed class MbrBuilder
{
    private readonly List<MbrEntry> _entries = [];
    private byte[] _bootstrap = BootMessageStub.ForMbr();
    private uint _signature;
    private ChsGeometry _geometry = ChsGeometry.Translated;

    /// <summary>Replaces the "Missing operating system" stub; at most 440 bytes so the signature and table stay intact.</summary>
    public MbrBuilder WithBootstrap(ReadOnlySpan<byte> code)
    {
        if (code.Length > Mbr.BootstrapLength)
        {
            throw new ArgumentException($"Boot code of {code.Length} bytes exceeds {Mbr.BootstrapLength}.", nameof(code));
        }

        _bootstrap = code.ToArray();
        return this;
    }

    public MbrBuilder WithSignature(uint signature)
    {
        _signature = signature;
        return this;
    }

    public MbrBuilder WithGeometry(ChsGeometry geometry)
    {
        _geometry = geometry;
        return this;
    }

    public MbrBuilder AddPartition(byte type, long startLba, long sectorCount, bool active = false)
    {
        if (_entries.Count == Mbr.EntryCount)
        {
            throw new InvalidOperationException("An MBR holds at most four partitions.");
        }

        if (type == MbrPartitionType.Empty)
        {
            throw new ArgumentException("Type 0 marks an unused slot.", nameof(type));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(startLba, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sectorCount, 1);
        if (startLba + sectorCount > (1L << 32))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "The partition does not fit the 32-bit LBA fields of an MBR.");
        }

        if (active && _entries.Any(entry => entry.IsActive))
        {
            throw new InvalidOperationException("Only one partition can be active; boot code refuses to choose.");
        }

        var overlap = _entries.FirstOrDefault(entry => startLba < entry.EndLba && entry.StartLba < startLba + sectorCount);
        if (overlap is not null)
        {
            throw new InvalidOperationException($"The partition at LBA {startLba} overlaps the one at LBA {overlap.StartLba}.");
        }

        _entries.Add(new MbrEntry(
            active ? MbrEntry.ActiveStatus : (byte)0,
            type,
            (uint)startLba,
            (uint)sectorCount,
            _geometry.FromLba(startLba),
            _geometry.FromLba(startLba + sectorCount - 1)));
        return this;
    }

    public Mbr Build()
    {
        var bootstrap = new byte[Mbr.BootstrapLength];
        _bootstrap.CopyTo(bootstrap, 0);

        var entries = new MbrEntry[Mbr.EntryCount];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = i < _entries.Count ? _entries[i] : MbrEntry.Empty;
        }

        return new Mbr { Bootstrap = bootstrap, DiskSignature = _signature, Entries = entries };
    }

    /// <summary>
    /// The MBR of a GPT disk: one 0xEE entry from LBA 1 to the end, so tools that only know MBR
    /// see a fully used disk. Sizes beyond 32 bits are clamped, the end CHS becomes 0xFFFFFF.
    /// </summary>
    public static Mbr Protective(long totalSectors, ChsGeometry? geometry = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSectors, 2);
        var size = Math.Min(totalSectors - 1, uint.MaxValue);
        var end = (geometry ?? ChsGeometry.Translated).FromLba(totalSectors - 1);
        if (end == ChsAddress.Unrepresentable)
        {
            end = ChsAddress.ProtectiveEnd;
        }

        var entry = new MbrEntry(0, MbrPartitionType.GptProtective, 1, (uint)size, ChsAddress.ProtectiveStart, end);
        return new Mbr { Entries = [entry, MbrEntry.Empty, MbrEntry.Empty, MbrEntry.Empty] };
    }
}
