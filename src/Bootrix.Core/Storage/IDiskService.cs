// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

public sealed record DiskFilter
{
    /// <summary>Include USB hard disks and SSDs, which Windows reports as fixed media.</summary>
    public bool IncludeUsbHardDisks { get; init; }

    /// <summary>Include internal disks (service mode). System, boot and pagefile disks stay blocked either way.</summary>
    public bool IncludeInternalDisks { get; init; }

    public bool IncludeVirtualDisks { get; init; }

    /// <summary>List blocked disks as well so the UI can show them greyed out with the reason.</summary>
    public bool IncludeBlocked { get; init; } = true;

    public bool IncludeEmptyReaders { get; init; }
}

public interface IDiskService
{
    IReadOnlyList<StorageDevice> Enumerate(DiskFilter filter);

    StorageDevice? Find(string devicePath);

    /// <summary>Raised (debounced) when a disk appears or disappears.</summary>
    event EventHandler? DevicesChanged;
}
