// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;

namespace Bootrix.Core.Optical.Reading;

/// <summary>
/// What is needed to continue an interrupted rip. The image file itself holds the data; the hash
/// is not stored because a running SHA-256 cannot be saved, the resume hashes the written part again.
/// </summary>
public sealed record RipCheckpoint
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Sectors the rip was going to read; a different drive capacity means a different disc.</summary>
    public long SectorCount { get; init; }

    /// <summary>First sector not yet written. Everything before it is final.</summary>
    public long NextSector { get; init; }

    public IReadOnlyList<SectorRange> BadSectors { get; init; } = [];
}

public interface IRipCheckpointStore
{
    RipCheckpoint? Load();

    void Save(RipCheckpoint checkpoint);

    void Clear();
}

/// <summary>Keeps the checkpoint next to the image. Written through a temporary file so that a power cut never leaves half a checkpoint.</summary>
public sealed class FileRipCheckpointStore(string path) : IRipCheckpointStore
{
    public string Path { get; } = path;

    public RipCheckpoint? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            var checkpoint = JsonSerializer.Deserialize<RipCheckpoint>(File.ReadAllText(Path), CoreJson.Options);
            return checkpoint is { Version: RipCheckpoint.CurrentVersion } ? checkpoint : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable checkpoint only costs the resume, never the rip.
            return null;
        }
    }

    public void Save(RipCheckpoint checkpoint)
    {
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, CoreJson.Options));
        File.Move(temp, Path, overwrite: true);
    }

    public void Clear()
    {
        File.Delete(Path);
    }
}
