// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Capture;
using Bootrix.Core.Workshop.Hardware;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Lists the third-party driver packages of the driver store: the oemNN.inf files in the INF folder, which are what
/// `pnputil /enum-drivers` shows as published names. Exporting the packages is a later step.
/// </summary>
internal static class DriverStoreReader
{
    private const long MaximumInfBytes = 16L << 20;

    public static IReadOnlyList<ThirdPartyDriver> Read(ILogger logger, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var drivers = new List<ThirdPartyDriver>();

        foreach (var file in Directory.EnumerateFiles(folder, "oem*.inf"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            try
            {
                // The pattern also matches names such as oem-foo.inf; only oemNN.inf is a published package.
                if (!DriverPackageInfo.IsOemInf(name) || new FileInfo(file).Length > MaximumInfBytes)
                {
                    continue;
                }

                if (InfFileParser.Parse(name, File.ReadAllBytes(file)) is { } driver)
                {
                    drivers.Add(driver);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping {File}", name);
            }
        }

        return [.. drivers.OrderBy(d => d.PublishedName, StringComparer.OrdinalIgnoreCase)];
    }
}
