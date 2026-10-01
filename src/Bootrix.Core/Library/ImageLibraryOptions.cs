// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

public sealed record ImageLibraryOptions
{
    /// <summary>The folder Bootrix stores its downloads in. Created when the first image is added.</summary>
    public required string LocalDirectory { get; init; }

    /// <summary>
    /// An optional second folder that is only read: a network share (<c>\\nas\bootrix-cache</c>) that another
    /// installation's library writes to. Its images are found like local ones, and never changed or removed from here.
    /// </summary>
    public string? SharedDirectory { get; init; }
}
