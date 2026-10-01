// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

/// <summary>The numeric value is the width of a FAT entry in bits.</summary>
public enum FatType
{
    Fat12 = 12,
    Fat16 = 16,
    Fat32 = 32,
}
