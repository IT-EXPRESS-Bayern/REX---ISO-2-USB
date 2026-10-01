// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Library;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Library;

/// <summary>A scratch area with a local library folder, a "share", an inbox for files to add and a clock the test controls.</summary>
internal sealed class LibraryFixture : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly List<ImageLibrary> _libraries = [];

    public LibraryFixture()
    {
        Directory.CreateDirectory(Inbox);
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public string Local => Path.Combine(_root.Path, "library");

    public string Shared => Path.Combine(_root.Path, "nas");

    public string Inbox => Path.Combine(_root.Path, "inbox");

    public ImageLibrary Library(bool withShared = false)
    {
        var library = new ImageLibrary(
            new ImageLibraryOptions { LocalDirectory = Local, SharedDirectory = withShared ? Shared : null },
            NullLogger<ImageLibrary>.Instance,
            Clock);
        _libraries.Add(library);
        return library;
    }

    /// <summary>Another installation whose library folder is the share, which is how the share gets its content.</summary>
    public ImageLibrary Colleague()
    {
        var library = new ImageLibrary(new ImageLibraryOptions { LocalDirectory = Shared }, NullLogger<ImageLibrary>.Instance, Clock);
        _libraries.Add(library);
        return library;
    }

    /// <summary>Creates a file of deterministic content in the inbox; the same seed gives the same bytes and therefore the same hash.</summary>
    public (string Path, string Sha256) File(string name, int length = 4096, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);

        var path = System.IO.Path.Combine(Inbox, name);
        System.IO.File.WriteAllBytes(path, data);
        return (path, Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    public static string ImagePath(string directory, string sha256, string extension = "iso") =>
        System.IO.Path.Combine(directory, $"{sha256}.{extension}");

    public static string MetadataPath(string directory, string sha256) => System.IO.Path.Combine(directory, sha256 + ".json");

    /// <summary>Name, length, last write time and a digest of every file: what must not change in a folder that is only read.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot(string directory) =>
        !Directory.Exists(directory)
            ? new Dictionary<string, string>()
            : Directory.GetFiles(directory).ToDictionary(
                f => System.IO.Path.GetFileName(f),
                f => $"{new FileInfo(f).Length}|{System.IO.File.GetLastWriteTimeUtc(f):O}|{Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(f)))}");

    public void Dispose()
    {
        foreach (var library in _libraries)
        {
            library.Dispose();
        }

        _root.Dispose();
    }
}
