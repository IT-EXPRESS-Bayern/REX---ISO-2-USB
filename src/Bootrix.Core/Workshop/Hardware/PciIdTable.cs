// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

public sealed record PciIdEntry(ushort VendorId, ushort DeviceId, string Name);

/// <summary>A list of PCI devices with the reference it was compiled from, so every entry can be traced to a source.</summary>
public sealed class PciIdTable
{
    private readonly Dictionary<uint, PciIdEntry> _byId;

    public PciIdTable(string description, string source, IEnumerable<PciIdEntry> entries)
    {
        Description = description;
        Source = source;
        Entries = [.. entries];
        _byId = Entries.ToDictionary(e => Key(e.VendorId, e.DeviceId));
    }

    public string Description { get; }

    public string Source { get; }

    public IReadOnlyList<PciIdEntry> Entries { get; }

    public bool Contains(PciId id) => _byId.ContainsKey(Key(id.VendorId, id.DeviceId));

    public PciIdEntry? Find(PciId id) => _byId.GetValueOrDefault(Key(id.VendorId, id.DeviceId));

    private static uint Key(ushort vendor, ushort device) => ((uint)vendor << 16) | device;
}
