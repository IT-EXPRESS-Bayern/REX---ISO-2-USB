// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Windows;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Tests.Writing.Windows;

/// <summary>A temporary medium and work folder.</summary>
internal sealed class MediaFolder : IDisposable
{
    public MediaFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "bootrix-wincust-" + Guid.NewGuid().ToString("N"));
        Media = Path.Combine(Root, "media");
        Work = Path.Combine(Root, "work");
        Directory.CreateDirectory(Media);
        Directory.CreateDirectory(Work);
    }

    public string Root { get; }

    public string Media { get; }

    public string Work { get; }

    public string Full(string relative) => Path.Combine(Media, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
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
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that stays behind is not worth failing a test for.
        }
    }
}

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

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];

    public string All
    {
        get
        {
            lock (_lines)
            {
                return string.Join('\n', _lines);
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : " " + exception));
        }
    }
}

/// <summary>Minimal WIM files: a header and the XML data, enough for the metadata reader.</summary>
internal static class FakeWim
{
    private const int HeaderSize = 208;

    public static string Image(int index, string name, string editionId = "WindowsPE", int build = 26200) =>
        $"<IMAGE INDEX=\"{index}\"><DIRCOUNT>1</DIRCOUNT><FILECOUNT>1</FILECOUNT><TOTALBYTES>1000</TOTALBYTES>"
        + $"<WINDOWS><ARCH>9</ARCH><EDITIONID>{editionId}</EDITIONID><INSTALLATIONTYPE>Client</INSTALLATIONTYPE>"
        + $"<VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>{build}</BUILD></VERSION></WINDOWS><NAME>{name}</NAME></IMAGE>";

    public static byte[] Build(params string[] images)
    {
        var xml = "<WIM>" + string.Concat(images) + "<TOTALBYTES>1000</TOTALBYTES></WIM>";
        var xmlBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        var file = new byte[HeaderSize + xmlBytes.Length];
        "MSWIM\0\0\0"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), 0x10D00);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10), 0x2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x14), 32768);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x28), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x2A), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x2C), (uint)images.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0x48), (uint)xmlBytes.Length | (0x2UL << 56));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0x50), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0x58), (ulong)xmlBytes.Length);
        xmlBytes.CopyTo(file.AsSpan(HeaderSize));
        return file;
    }

    public static byte[] BootWim() => Build(Image(1, "Microsoft Windows PE (x64)"), Image(2, "Microsoft Windows Setup (x64)"));

    public static byte[] InstallWim(params (string Name, string EditionId)[] editions) =>
        Build([.. editions.Select((e, i) => Image(i + 1, e.Name, e.EditionId))]);

    public static WimEdition Edition(int index, string name, string editionId) =>
        new() { Index = index, Name = name, EditionId = editionId, DisplayName = name };
}

/// <summary>Stands in for DISM and the registry; see the Core tests for the same idea.</summary>
internal sealed class FakeImageTools : IImageServicing, IImageFileSystem
{
    public List<string> Calls { get; } = [];

    public List<string> Registry { get; } = [];

    public long FreeBytes { get; set; } = long.MaxValue;

    public Task<IMountedImage> MountAsync(string imagePath, int index, string mountDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls.Add($"mount:{Path.GetFileName(imagePath)}:{index}");
        Directory.CreateDirectory(mountDirectory);
        progress?.Report(1);
        return Task.FromResult<IMountedImage>(new Mounted(this, imagePath, index, mountDirectory));
    }

    public IOfflineHive LoadHive(string mountDirectory, RegistryHive hive) => new Hive(this, hive);

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
        public void SetValue(string key, string name, RegistryValueKind kind, string value) => owner.Registry.Add($"{hive}:{key}\\{name}={value}");

        public void DeleteKey(string key) => owner.Registry.Add($"{hive}:-{key}");

        public void DeleteValue(string key, string name) => owner.Registry.Add($"{hive}:-{key}\\{name}");

        public void Dispose()
        {
        }
    }
}

/// <summary>Builds the objects a customizer is handed, without a disk or an ISO.</summary>
internal static class CustomizationKit
{
    public static StorageDevice Stick(string? serial = "0123456789AB", int number = 3) => new()
    {
        DiskNumber = number,
        DevicePath = @"\\?\usbstor#disk&ven_test#" + number,
        Serial = serial,
        SizeBytes = 16L << 30,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
    };

    public static ImageProfile Profile(ImageKind kind = ImageKind.WindowsSetup, int build = 26200, WindowsArch arch = WindowsArch.X64) => new()
    {
        Kind = kind,
        Arch = arch,
        WindowsBuild = build,
        HasEfiBootFiles = true,
        HasBiosBootFiles = true,
    };

    public static MediaWriteContext Write(
        string work,
        WindowsSetupOptions? windows = null,
        ImageProfile? image = null,
        IReadOnlyList<WimEdition>? editions = null,
        int targets = 1,
        string? password = null,
        TargetFirmware firmware = TargetFirmware.BiosAndUefi)
    {
        var profile = image ?? Profile();
        var info = new WindowsImageInfo
        {
            Images = editions is null
                ? []
                : [new WindowsImageFile("sources/install.wim", 1000, WimMetadata.Read(new MemoryStream(FakeWim.Build([.. editions.Select(e => FakeWim.Image(e.Index, e.Name ?? "", e.EditionId ?? "Professional"))]))))],
        };

        var devices = Enumerable.Range(1, targets).Select(i => Stick($"SERIAL{i:D6}", i)).ToList();
        return new MediaWriteContext
        {
            JobId = "write-test",
            ImagePath = "windows.iso",
            Inspection = new ImageInspection { Profile = profile, Container = ImageContainer.IsoUdfBridge, Windows = info },
            Spec = new JobSpec { Windows = windows ?? new WindowsSetupOptions() },
            Targets =
            [
                .. devices.Select(device => new MediaWriteTarget
                {
                    Device = device,
                    Identity = DiskIdentity.From(device),
                    Plan = new MediaPlan { WriteMethod = WriteMethod.ExtractFiles, Firmware = firmware },
                }),
            ],
            WorkDirectory = work,
            LocalAccountPassword = password,
        };
    }

    public static WindowsMediaCustomization Customization(MediaWriteContext write, string mediaRoot, int target = 0, WindowsArch? arch = null) => new()
    {
        Write = write,
        Target = write.Targets[target],
        MediaRoot = mediaRoot,
        Arch = arch ?? write.Image.Arch,
        Build = write.Image.WindowsBuild,
        WorkDirectory = Path.Combine(Path.GetDirectoryName(mediaRoot) ?? mediaRoot, "work"),
    };
}
