// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Optical.Images;

/// <param name="BinPath">Path of the BIN file with the track.</param>
/// <param name="StartSector">First sector of the track (INDEX 01) in the file.</param>
/// <param name="SectorCount">Sectors of user data.</param>
public sealed record CueDataPlan(string BinPath, CueTrackMode Mode, long StartSector, long SectorCount)
{
    public long UserDataBytes => SectorCount * SectorMath.SectorSize;
}

/// <summary>
/// Decides whether a BIN/CUE image can be burned as an ordinary data disc. That works for a single
/// MODE1 track, whose 2048 bytes of user data are exactly what an ISO image holds. Audio, MODE2/XA and
/// anything with more than one track would need a raw disc-at-once write with the full sector layout,
/// which Bootrix does not do, so those are refused instead of burning something that does not work.
/// </summary>
public static class CueBurnPlanner
{
    /// <param name="sheet">The parsed cue sheet.</param>
    /// <param name="cueDirectory">Folder the file names in the sheet are relative to.</param>
    /// <param name="fileLength">Returns the size of a file, or null if it does not exist.</param>
    public static CueDataPlan PlanDataDisc(CueSheet sheet, string cueDirectory, Func<string, long?> fileLength)
    {
        if (sheet.Tracks.Any(t => t.Mode == CueTrackMode.Audio))
        {
            throw Unsupported("the cue sheet has audio tracks; only a single MODE1 data track can be burned as a data disc");
        }

        var track = sheet.Tracks[0];
        if (sheet.Tracks.Count > 1)
        {
            throw Unsupported($"the cue sheet has {sheet.Tracks.Count} tracks; only a single data track can be burned as a data disc");
        }

        if (track.Mode is not (CueTrackMode.Mode1Data2048 or CueTrackMode.Mode1Raw2352))
        {
            throw Unsupported($"track {track.Number} is {track.Mode} (MODE2, CD-I and CD+G images cannot be burned as a data disc)");
        }

        if (!track.File.IsBinary)
        {
            throw Unsupported($"file '{track.File.Name}' is of type {track.File.Type}, not BINARY");
        }

        var path = Path.GetFullPath(Path.Combine(cueDirectory, track.File.Name.Replace('\\', Path.DirectorySeparatorChar)));
        var length = fileLength(path)
            ?? throw new BootrixException(ErrorCode.ImageUnreadable, $"BIN file '{path}' does not exist") { Arguments = [$"BIN file '{track.File.Name}' not found next to the cue sheet"] };

        var sectorSize = track.Mode.SectorSize();
        var start = track.Start!.Position.ToFrames();
        var available = length / sectorSize - start;
        if (available <= 0)
        {
            throw new BootrixException(ErrorCode.ImageTruncated, path);
        }

        return new CueDataPlan(path, track.Mode, start, available);
    }

    /// <summary>Opens the 2048-byte view of the planned track. The stream owns the BIN file.</summary>
    public static Stream Open(CueDataPlan plan, Func<string, Stream> openFile) =>
        new Mode1UserDataStream(openFile(plan.BinPath), plan.Mode == CueTrackMode.Mode1Raw2352, plan.StartSector, plan.SectorCount);

    /// <summary>Plans a cue file on disk and checks the first, middle and last sector so a mislabelled BIN is caught before a disc is wasted.</summary>
    public static DiscImageSource FromFile(string cuePath)
    {
        var sheet = CueParser.ParseFile(cuePath);
        var plan = PlanDataDisc(
            sheet,
            Path.GetDirectoryName(Path.GetFullPath(cuePath))!,
            path => File.Exists(path) ? new FileInfo(path).Length : null);

        using (var probe = Open(plan, OpenForRead))
        {
            var sector = new byte[SectorMath.SectorSize];
            foreach (var index in new[] { 0L, plan.SectorCount / 2, plan.SectorCount - 1 }.Distinct())
            {
                probe.Position = index * SectorMath.SectorSize;
                probe.ReadExactly(sector);
            }
        }

        return new DiscImageSource(
            Path.GetFileName(cuePath),
            DiscImageKind.BinCue,
            plan.UserDataBytes,
            () => Open(plan, OpenForRead));
    }

    private static FileStream OpenForRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

    private static BootrixException Unsupported(string reason) =>
        new(ErrorCode.ImageUnsupported, reason) { Arguments = [reason] };
}
