// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>A GPT partition as the MBR half of a hybrid disk sees it.</summary>
public sealed record HybridEntry(byte MbrType, long StartLba, long SectorCount, bool Active = false);
