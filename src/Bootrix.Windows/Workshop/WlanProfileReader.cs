// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Windows.Workshop;

/// <summary>Saved Wi-Fi profiles through netsh. Listing reads names only; the export is a separate, explicit step.</summary>
internal static class WlanProfileReader
{
    public static async Task<IReadOnlyList<WlanProfile>> ListAsync(CancellationToken cancellationToken)
    {
        var (_, output) = await SystemTool.RunAsync("netsh.exe", ["wlan", "show", "profiles"], cancellationToken).ConfigureAwait(false);

        // netsh exits with an error text, not an exit code we could rely on, when the machine has no Wi-Fi interface; no names then.
        return [.. NetshWlanParser.ParseProfileNames(output).Select(name => new WlanProfile { Name = name })];
    }

    /// <summary>
    /// Exports all profiles with their keys in clear text into <paramref name="directory"/>. One call for all profiles keeps the names
    /// intact: the XML files are UTF-8, whereas the console output of netsh is in the OEM code page and loses characters.
    /// </summary>
    public static async Task<IReadOnlyList<WlanProfile>> ExportAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var startedAt = DateTime.UtcNow.AddSeconds(-2);

        await SystemTool.RunAsync("netsh.exe", ["wlan", "export", "profile", $"folder={directory}", "key=clear"], cancellationToken).ConfigureAwait(false);

        var profiles = new List<WlanProfile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml"))
        {
            // Files that were already there are not part of this export.
            if (File.GetLastWriteTimeUtc(file) < startedAt)
            {
                continue;
            }

            var profile = WlanProfileXml.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), file);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }
}
