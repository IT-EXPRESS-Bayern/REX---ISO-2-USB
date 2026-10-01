// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Settings;

/// <summary>
/// Keeps the user settings in one JSON file. A damaged file is moved aside and replaced by defaults,
/// because a settings problem must never keep the program from starting.
/// </summary>
public sealed class SettingsStore(string path, ILogger<SettingsStore>? logger = null)
{
    private readonly Lock _gate = new();
    private AppSettings? _current;

    public event EventHandler<AppSettings>? Changed;

    public string Path { get; } = path;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_gate)
        {
            _current = updated = change(_current ??= Load());
            Save(updated);
        }

        Changed?.Invoke(this, updated);
    }

    private AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize<AppSettings>(stream, CoreJson.Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Settings file {Path} could not be read, using defaults", Path);
            MoveAside();
            return new AppSettings();
        }
    }

    private void MoveAside()
    {
        try
        {
            File.Move(Path, Path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Damaged settings file could not be moved aside");
        }
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, CoreJson.Options));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The values stay active for this session; losing persistence is not worth an error dialog.
            logger?.LogWarning(ex, "Settings could not be saved to {Path}", Path);
        }
    }
}
