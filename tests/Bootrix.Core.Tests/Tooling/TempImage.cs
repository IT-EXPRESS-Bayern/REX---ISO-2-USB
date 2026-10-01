// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Tooling;

/// <summary>A scratch image file whose length is set up front; large images stay sparse on Linux.</summary>
public sealed class TempImage : IDisposable
{
    public TempImage(long length)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bootrix-test-{Guid.NewGuid():N}.img");
        using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(length);
    }

    public string Path { get; }

    public FileStream Open() =>
        new(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A leftover scratch file is not worth failing the test run for.
        }
    }
}
