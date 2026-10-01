// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Images.Apple;

/// <summary>An opened Apple image: the decoded volume plus what it was stored in.</summary>
internal sealed class OpenedImage(Stream volume, AppleImageContainer container, DmgInfo? dmg) : IDisposable
{
    public Stream Volume { get; } = volume;

    public AppleImageContainer Container { get; } = container;

    public DmgInfo? Dmg { get; } = dmg;

    public void Dispose() => Volume.Dispose();
}

/// <summary>Picks the reader for a path: UDIF, sparse image, sparse bundle, or the file itself as raw volume.</summary>
internal static class AppleImageOpener
{
    public static OpenedImage Open(string path, DmgReaderOptions? options, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Directory.Exists(path))
        {
            return new OpenedImage(SparseBundleStream.Open(path), AppleImageContainer.SparseBundle, null);
        }

        ContainerSignature signature;
        long length;
        using (var source = RandomAccessSource.OpenFile(path))
        {
            signature = ContainerSniffer.Detect(source);
            length = source.Length;
        }

        ContainerSniffer.ThrowIfUnsupported(signature);
        switch (signature)
        {
            case ContainerSignature.Udif:
                var dmg = DmgReader.Open(path, options, logger);
                return new OpenedImage(dmg, AppleImageContainer.Udif, dmg.Info);
            case ContainerSignature.SparseImage:
                return new OpenedImage(SparseImageStream.Open(path), AppleImageContainer.SparseImage, null);
            default:
                if (length == 0)
                {
                    throw ImageErrors.Unreadable("the file is empty");
                }

                return new OpenedImage(OpenRaw(path), AppleImageContainer.Raw, null);
        }
    }

    private static FileStream OpenRaw(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageErrors.Unreadable(ex.Message, ex);
        }
    }
}
