// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tiny;

public sealed record ProvisionedAppx(string PackageName, string DisplayName);

public sealed record WindowsPackage(string Identity, string State);

public sealed record WindowsCapability(string Name, string State);

public enum InstallImageCompression
{
    /// <summary>LZX, fast to apply, works for FAT32 media after splitting.</summary>
    Maximum,

    /// <summary>LZMS solid ("recovery"): the smallest result, but it cannot be split for FAT32.</summary>
    Recovery,
}

/// <summary>A mounted Windows image. Disposing without <see cref="UnmountAsync"/> discards all changes.</summary>
public interface IMountedImage : IAsyncDisposable
{
    string MountDirectory { get; }

    Task UnmountAsync(bool commit, CancellationToken cancellationToken);
}

/// <summary>Image servicing through the Windows DISM API; implemented in the Windows layer.</summary>
public interface IImageServicing
{
    Task<IMountedImage> MountAsync(string imagePath, int index, string mountDirectory, IProgress<double>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(string mountDirectory, CancellationToken cancellationToken);

    Task RemoveProvisionedAppxAsync(string mountDirectory, string packageName, CancellationToken cancellationToken);

    Task<IReadOnlyList<WindowsPackage>> GetPackagesAsync(string mountDirectory, CancellationToken cancellationToken);

    Task RemovePackageAsync(string mountDirectory, string identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<WindowsCapability>> GetCapabilitiesAsync(string mountDirectory, CancellationToken cancellationToken);

    Task RemoveCapabilityAsync(string mountDirectory, string name, CancellationToken cancellationToken);

    /// <summary>The UI language of the image as a tag such as "de-DE"; used to pick the matching language feature packages.</summary>
    Task<string> GetDefaultLanguageAsync(string mountDirectory, CancellationToken cancellationToken);

    /// <summary>Removes superseded components (/StartComponentCleanup /ResetBase).</summary>
    Task CleanupComponentStoreAsync(string mountDirectory, CancellationToken cancellationToken);
}

/// <summary>An offline registry hive loaded from an image; disposing unloads it.</summary>
public interface IOfflineHive : IDisposable
{
    void SetValue(string key, string name, RegistryValueKind kind, string value);

    void DeleteKey(string key);

    void DeleteValue(string key, string name);
}

public interface IImageFileSystem
{
    IOfflineHive LoadHive(string mountDirectory, RegistryHive hive);

    /// <summary>Deletes a file or folder inside the mounted image, taking ownership first when the owner is TrustedInstaller.</summary>
    Task DeleteAsync(string path, bool takeOwnership, CancellationToken cancellationToken);

    /// <summary>Replaces the file with an empty one (winre.wim).</summary>
    Task ReplaceWithEmptyFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Rebuilds the component store from the patterns that should survive; everything else is removed.</summary>
    Task RebuildWinSxsAsync(string winSxsPath, IReadOnlyList<string> keepPatterns, CancellationToken cancellationToken);

    long GetFreeBytes(string path);

    Task CopyDirectoryAsync(string source, string destination, Func<string, bool>? filter, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>WIM/ESD handling (wimlib) needed by the builder.</summary>
public interface IInstallImageTools
{
    /// <summary>Number of images and their names, read from the install image of the source media.</summary>
    Task<IReadOnlyList<InstallEdition>> GetEditionsAsync(string installImagePath, CancellationToken cancellationToken);

    /// <summary>Writes one edition of a WIM or ESD as a new single-image WIM.</summary>
    Task ExportEditionAsync(string source, int index, string destination, InstallImageCompression compression, IProgress<double>? progress, CancellationToken cancellationToken);

    Task<string> GetArchitectureAsync(string imagePath, int index, CancellationToken cancellationToken);
}

public sealed record InstallEdition(int Index, string Name, string? Description, long TotalBytes);

/// <summary>Creates the bootable ISO from the finished media folder (oscdimg or an equivalent).</summary>
public interface IIsoWriter
{
    Task CreateAsync(string mediaDirectory, string isoPath, string volumeLabel, bool uefi2023, IProgress<double>? progress, CancellationToken cancellationToken);
}
