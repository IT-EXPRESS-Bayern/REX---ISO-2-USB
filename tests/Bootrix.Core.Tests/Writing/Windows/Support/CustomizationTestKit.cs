// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Tests.Images.Wim;
using Bootrix.Core.Tiny;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Tests.Writing.Windows.Support;

/// <summary>A temporary folder that holds a medium and a work area, and is gone after the test.</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "bootrix-cust-" + Guid.NewGuid().ToString("N"));
        Media = Path.Combine(Root, "media");
        Work = Path.Combine(Root, "work");
        Directory.CreateDirectory(Media);
        Directory.CreateDirectory(Work);
    }

    public string Root { get; }

    public string Media { get; }

    public string Work { get; }

    public string Full(string relative) => Path.Combine(Media, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content = "x")
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Write(string relative, byte[] content)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string Read(string relative) => File.ReadAllText(Full(relative));

    public bool Exists(string relative) => File.Exists(Full(relative)) || Directory.Exists(Full(relative));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that stays behind is not worth failing a test for.
        }
    }
}

/// <summary>Collects progress values synchronously, so a test sees exactly what a customizer reported.</summary>
internal sealed class ProgressLog : IProgress<double>
{
    private readonly Lock _gate = new();
    private readonly List<double> _values = [];

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_gate)
            {
                return [.. _values];
            }
        }
    }

    public void Report(double value)
    {
        lock (_gate)
        {
            _values.Add(value);
        }
    }

    /// <summary>Progress only grows, stays within 0..1 and ends at 1.</summary>
    public void AssertMonotonicToOne()
    {
        var values = Values;
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.InRange(value, 0.0, 1.0));
        for (var i = 1; i < values.Count; i++)
        {
            Assert.True(values[i] >= values[i - 1], $"progress went back from {values[i - 1]} to {values[i]}");
        }

        Assert.Equal(1.0, values[^1], 6);
    }
}

/// <summary>A logger that keeps what was written, to check that nothing secret ends up in it.</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : " " + exception));
        }
    }

    public string All => string.Join('\n', Lines);

    public bool Contains(string text) => All.Contains(text, StringComparison.Ordinal);
}

internal static class FakeWims
{
    public static byte[] BootWim(bool withSetup = true)
    {
        var images = new List<string> { WimFixture.Image(1, "Microsoft Windows PE (x64)", 9, 26200, "WindowsPE") };
        if (withSetup)
        {
            images.Add(WimFixture.Image(2, "Microsoft Windows Setup (x64)", 9, 26200, "WindowsPE"));
        }

        return WimFixture.Build(WimFixture.Xml([.. images]));
    }

    public static byte[] InstallWim(params (string Name, string EditionId)[] editions) =>
        WimFixture.Build(WimFixture.Xml([.. editions.Select((e, i) => WimFixture.Image(i + 1, e.Name, 9, 26200, e.EditionId))]));

    public static WimEdition Edition(int index, string name, string? editionId = null) =>
        new() { Index = index, Name = name, EditionId = editionId, DisplayName = name };
}

/// <summary>
/// Stands in for DISM and the registry: "mounting" an image means creating the folder, committing appends a marker
/// to the image file, and every call is recorded in order.
/// </summary>
internal sealed class FakeImageTools : IImageServicing, IImageFileSystem
{
    public List<string> Calls { get; } = [];

    /// <summary>Registry operations as "hive:key\name=value".</summary>
    public List<string> Registry { get; } = [];

    public long FreeBytes { get; set; } = long.MaxValue;

    /// <summary>Runs inside every mount, to let a test cancel or throw at a chosen moment.</summary>
    public Action<string, int>? OnMount { get; set; }

    /// <summary>The folders the images were mounted at, to check that they were never on the medium.</summary>
    public List<string> ImagePaths { get; } = [];

    public Task<IMountedImage> MountAsync(string imagePath, int index, string mountDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls.Add($"mount:{Path.GetFileName(imagePath)}:{index}");
        ImagePaths.Add(imagePath);
        Directory.CreateDirectory(mountDirectory);
        progress?.Report(0.5);
        progress?.Report(1);
        OnMount?.Invoke(imagePath, index);
        return Task.FromResult<IMountedImage>(new Mounted(this, imagePath, index, mountDirectory));
    }

    public IOfflineHive LoadHive(string mountDirectory, RegistryHive hive)
    {
        Calls.Add($"load-hive:{hive}");
        return new Hive(this, hive);
    }

    public long GetFreeBytes(string path) => FreeBytes;

    public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(string mountDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RemoveProvisionedAppxAsync(string mountDirectory, string packageName, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<WindowsPackage>> GetPackagesAsync(string mountDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RemovePackageAsync(string mountDirectory, string identity, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<WindowsCapability>> GetCapabilitiesAsync(string mountDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RemoveCapabilityAsync(string mountDirectory, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CleanupComponentStoreAsync(string mountDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(string path, bool takeOwnership, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReplaceWithEmptyFileAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RebuildWinSxsAsync(string winSxsPath, IReadOnlyList<string> keepPatterns, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CopyDirectoryAsync(string source, string destination, Func<string, bool>? filter, IProgress<double>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

    private sealed class Mounted(FakeImageTools owner, string imagePath, int index, string mountDirectory) : IMountedImage
    {
        private bool _done;

        public string MountDirectory => mountDirectory;

        public Task UnmountAsync(bool commit, CancellationToken cancellationToken)
        {
            owner.Calls.Add(commit ? $"unmount:commit:{index}" : $"unmount:discard:{index}");
            if (commit)
            {
                File.AppendAllText(imagePath, $"+committed{index}", Encoding.ASCII);
            }

            _done = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_done)
            {
                owner.Calls.Add($"discard:{index}");
                _done = true;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Hive(FakeImageTools owner, RegistryHive hive) : IOfflineHive
    {
        public void SetValue(string key, string name, RegistryValueKind kind, string value) =>
            owner.Registry.Add($"{hive}:{key}\\{name}={value}:{kind}");

        public void DeleteKey(string key) => owner.Registry.Add($"{hive}:-{key}");

        public void DeleteValue(string key, string name) => owner.Registry.Add($"{hive}:-{key}\\{name}");

        public void Dispose() => owner.Calls.Add($"unload-hive:{hive}");
    }
}
