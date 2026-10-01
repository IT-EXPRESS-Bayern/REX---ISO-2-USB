// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;

namespace Bootrix.Core.Net;

/// <summary>A small JSON file with one number per channel, replaced atomically on every change.</summary>
public sealed class FileVersionStore(string path) : IVersionStore
{
    private readonly object _gate = new();

    public long GetHighestVersion(string channel)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(channel);
        }
    }

    public void Record(string channel, long version)
    {
        lock (_gate)
        {
            var versions = Load();
            if (versions.GetValueOrDefault(channel) >= version)
            {
                return;
            }

            versions[channel] = version;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, versions, CoreJson.Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>An unreadable file counts as empty; the signature and expiry checks still apply, only the rollback memory is lost.</summary>
    private Dictionary<string, long> Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, long>>(stream, CoreJson.Options) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return [];
        }
    }
}
