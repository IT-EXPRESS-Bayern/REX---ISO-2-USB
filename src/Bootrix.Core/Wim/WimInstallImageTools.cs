// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Tiny;

namespace Bootrix.Core.Wim;

/// <summary>Reads and rewrites install images with wimlib; independent of the platform, so it can be tested on any system with the library.</summary>
public sealed class WimInstallImageTools : IInstallImageTools
{
    public Task<IReadOnlyList<InstallEdition>> GetEditionsAsync(string installImagePath, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<InstallEdition>>(
            () =>
            {
                using var wim = WimFile.Open(installImagePath);
                var editions = new List<InstallEdition>();
                for (var index = 1; index <= wim.Info.ImageCount; index++)
                {
                    editions.Add(new InstallEdition(
                        index,
                        wim.GetImageProperty(index, "NAME") ?? $"Image {index}",
                        wim.GetImageProperty(index, "DESCRIPTION"),
                        long.TryParse(wim.GetImageProperty(index, "TOTALBYTES"), CultureInfo.InvariantCulture, out var bytes) ? bytes : 0));
                }

                return editions;
            },
            cancellationToken);
    }

    public Task ExportEditionAsync(string source, int index, string destination, InstallImageCompression compression, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
                using var wim = WimFile.Open(source);

                // Always recompress: the source may be an ESD (solid LZMS) that Setup and FAT32 splitting cannot use as it is.
                var recovery = compression == InstallImageCompression.Recovery;
                wim.WriteImage(
                    destination,
                    index,
                    recovery ? WimCompression.Lzms : WimCompression.Lzx,
                    recompress: true,
                    solid: recovery,
                    progress,
                    cancellationToken);
            },
            cancellationToken);
    }

    public Task<string> GetArchitectureAsync(string imagePath, int index, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                using var wim = WimFile.Open(imagePath);
                return MapArchitecture(wim.GetImageProperty(index, "WINDOWS/ARCH"));
            },
            cancellationToken);
    }

    public Task<string> GetDefaultLanguageAsync(string imagePath, int index, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                using var wim = WimFile.Open(imagePath);
                return wim.GetImageProperty(index, "WINDOWS/LANGUAGES/DEFAULT") ?? "en-US";
            },
            cancellationToken);
    }

    /// <summary>The numbers are PROCESSOR_ARCHITECTURE values stored in the image XML.</summary>
    internal static string MapArchitecture(string? value) => value switch
    {
        "0" => "x86",
        "5" => "arm",
        "9" => "amd64",
        "12" => "arm64",
        _ => "amd64",
    };
}
