// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>The partition map of an Apple Partition Map disk.</summary>
/// <param name="BlockSize">Distance between map entries in bytes; the unit of the start and length fields.</param>
/// <param name="HasDriverDescriptor">Block 0 carries the "ER" Driver Descriptor Record.</param>
/// <param name="DeviceBlocks">Block count claimed by the driver descriptor; unreliable on optical media.</param>
public sealed record ApmMap(int BlockSize, bool HasDriverDescriptor, long DeviceBlocks, IReadOnlyList<ApmPartition> Partitions);
