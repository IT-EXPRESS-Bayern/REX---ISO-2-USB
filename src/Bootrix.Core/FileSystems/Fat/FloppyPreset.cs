// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

/// <summary>
/// The classic diskette formats with the BPB values DOS wrote for them. Everything but the
/// media byte could be derived from the size, but old BIOSes and boot sectors expect exactly these.
/// </summary>
public sealed record FloppyPreset(
    string Name,
    int TotalSectors,
    int SectorsPerCluster,
    int RootEntries,
    byte MediaDescriptor,
    int SectorsPerFat,
    int SectorsPerTrack,
    int Heads)
{
    private const int SectorBytes = 512;

    public static IReadOnlyList<FloppyPreset> All { get; } =
    [
        new("160K", 320, 1, 64, 0xFE, 1, 8, 1),
        new("180K", 360, 1, 64, 0xFC, 2, 9, 1),
        new("320K", 640, 2, 112, 0xFF, 1, 8, 2),
        new("360K", 720, 2, 112, 0xFD, 2, 9, 2),
        new("720K", 1440, 2, 112, 0xF9, 3, 9, 2),
        new("1.2M", 2400, 1, 224, 0xF9, 7, 15, 2),
        new("1.44M", 2880, 1, 224, 0xF0, 9, 18, 2),
        new("2.88M", 5760, 2, 240, 0xF0, 9, 36, 2),
    ];

    public long TotalBytes => (long)TotalSectors * SectorBytes;

    public static FloppyPreset? FromSize(long bytes) => All.FirstOrDefault(preset => preset.TotalBytes == bytes);

    public FatFormatOptions ToOptions() => new()
    {
        TotalBytes = TotalBytes,
        BytesPerSector = SectorBytes,
        SectorsPerCluster = SectorsPerCluster,
        ReservedSectors = 1,
        RootEntries = RootEntries,
        Type = FatType.Fat12,
        MediaDescriptor = MediaDescriptor,
        DriveNumber = 0,
        SectorsPerTrack = SectorsPerTrack,
        Heads = Heads,
        OemName = "MSDOS5.0",
    };
}
