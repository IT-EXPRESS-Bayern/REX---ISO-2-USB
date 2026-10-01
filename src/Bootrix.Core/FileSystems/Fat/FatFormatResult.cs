// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

/// <param name="Layout">The geometry that was written.</param>
/// <param name="VolumeId">The serial number stored in the boot sector, generated or as requested.</param>
/// <param name="Label">The label as stored on disk (upper case, OEM characters only); empty when none was set.</param>
public sealed record FatFormatResult(FatLayout Layout, uint VolumeId, string Label);
