// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Core.Writing.Raw;

public static class BlockMapLocator
{
    /// <summary>
    /// bmaptool writes <c>image.img.bmap</c> next to <c>image.img.gz</c> (and next to <c>image.img</c> itself); the
    /// name without the compression suffix and a plain <c>image.bmap</c> are accepted as well.
    /// </summary>
    public static string? Find(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        var bare = ImageFileTypes.IsCompressed(imagePath) ? Path.ChangeExtension(imagePath, null) : imagePath;
        return new[] { imagePath + ".bmap", bare + ".bmap", Path.ChangeExtension(bare, ".bmap") }
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(File.Exists);
    }
}
