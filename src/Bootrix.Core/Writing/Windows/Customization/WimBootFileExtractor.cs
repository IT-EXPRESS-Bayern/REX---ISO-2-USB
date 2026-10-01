// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Wim;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>Takes single files and folders out of an image of a WIM.</summary>
public interface IBootFileExtractor
{
    /// <summary>Extracts <paramref name="imagePaths"/> of one image directly below <paramref name="destination"/>; paths the image lacks are skipped.</summary>
    Task ExtractAsync(string imagePath, int imageIndex, IReadOnlyList<string> imagePaths, string destination, CancellationToken cancellationToken);
}

/// <summary>Extraction with wimlib: no mount, no DISM, and only the requested files are decompressed.</summary>
public sealed class WimBootFileExtractor : IBootFileExtractor
{
    public Task ExtractAsync(string imagePath, int imageIndex, IReadOnlyList<string> imagePaths, string destination, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                Directory.CreateDirectory(destination);
                using var wim = WimFile.Open(imagePath);
                wim.ExtractPaths(imageIndex, destination, imagePaths, cancellationToken);
            },
            cancellationToken);
}
