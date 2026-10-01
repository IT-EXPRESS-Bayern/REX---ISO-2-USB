// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>A sparse image file in the temp directory that is removed on dispose.</summary>
internal sealed class TempImage : IDisposable
{
    public TempImage(long sizeBytes)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bootrix-ext-{Guid.NewGuid():N}.img");
        Stream = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        Stream.SetLength(sizeBytes);
    }

    public string Path { get; }

    public FileStream Stream { get; }

    /// <summary>Flushes and closes the stream so external tools see the final bytes.</summary>
    public string Close()
    {
        Stream.Flush();
        Stream.Dispose();
        return Path;
    }

    public void Dispose()
    {
        Stream.Dispose();
        File.Delete(Path);
    }
}
