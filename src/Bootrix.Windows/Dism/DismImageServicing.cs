// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tiny;
using MsDism = Microsoft.Dism;

namespace Bootrix.Windows.Dism;

/// <summary>Image servicing through the DISM API (dismapi.dll of the running Windows).</summary>
public sealed class DismImageServicing : IImageServicing
{
    private static readonly Lock Gate = new();
    private static bool _initialised;

    /// <summary>
    /// Initialises the DISM API and clears mount points that an earlier crashed run left behind.
    /// Must be called once per process before any other member is used.
    /// </summary>
    public static void EnsureInitialised()
    {
        lock (Gate)
        {
            if (_initialised)
            {
                return;
            }

            MsDism.DismApi.Initialize(MsDism.DismLogLevel.LogErrors);
            try
            {
                MsDism.DismApi.CleanupMountpoints();
            }
            catch (MsDism.DismException)
            {
                // Nothing to clean up, or another process owns the mounts.
            }

            _initialised = true;
        }
    }

    public Task<IMountedImage> MountAsync(string imagePath, int index, string mountDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        EnsureInitialised();
        return Task.Run<IMountedImage>(
            () =>
            {
                try
                {
                    MsDism.DismApi.MountImage(imagePath, mountDirectory, index, false, WrapProgress(progress, cancellationToken));
                }
                catch (MsDism.DismException ex)
                {
                    throw new BootrixException(ErrorCode.ImageMountFailed, $"mount {imagePath} #{index}: {ex.Message}", ex);
                }

                return new MountedWim(mountDirectory);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(string mountDirectory, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session => (IReadOnlyList<ProvisionedAppx>)
            [.. MsDism.DismApi.GetProvisionedAppxPackages(session).Select(a => new ProvisionedAppx(a.PackageName, a.DisplayName))], cancellationToken);

    public Task RemoveProvisionedAppxAsync(string mountDirectory, string packageName, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session =>
        {
            MsDism.DismApi.RemoveProvisionedAppxPackage(session, packageName);
            return 0;
        }, cancellationToken);

    public Task<IReadOnlyList<WindowsPackage>> GetPackagesAsync(string mountDirectory, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session => (IReadOnlyList<WindowsPackage>)
            [.. MsDism.DismApi.GetPackages(session).Select(p => new WindowsPackage(p.PackageName, p.PackageState.ToString()))], cancellationToken);

    public Task RemovePackageAsync(string mountDirectory, string identity, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session =>
        {
            MsDism.DismApi.RemovePackageByName(session, identity);
            return 0;
        }, cancellationToken);

    public Task<IReadOnlyList<WindowsCapability>> GetCapabilitiesAsync(string mountDirectory, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session => (IReadOnlyList<WindowsCapability>)
            [.. MsDism.DismApi.GetCapabilities(session).Select(c => new WindowsCapability(c.Name, c.State.ToString()))], cancellationToken);

    public Task RemoveCapabilityAsync(string mountDirectory, string name, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session =>
        {
            MsDism.DismApi.RemoveCapability(session, name);
            return 0;
        }, cancellationToken);

    public Task CleanupComponentStoreAsync(string mountDirectory, CancellationToken cancellationToken) =>
        InSession(mountDirectory, session =>
        {
            MsDism.DismApi.CleanImage(session, MsDism.DismCleanImageType.Component, MsDism.DismCleanImageFlags.ResetBase);
            return 0;
        }, cancellationToken);

    private static Task<T> InSession<T>(string mountDirectory, Func<MsDism.DismSession, T> action, CancellationToken cancellationToken)
    {
        EnsureInitialised();
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var session = MsDism.DismApi.OpenOfflineSession(mountDirectory);
                    return action(session);
                }
                catch (MsDism.DismException ex)
                {
                    throw new BootrixException(ErrorCode.ExternalToolFailed, ex.Message, ex)
                    {
                        Arguments = ["DISM", $"0x{ex.ErrorCode:X8} {ex.Message}"],
                    };
                }
            },
            cancellationToken);
    }

    private static MsDism.DismProgressCallback? WrapProgress(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (progress is null && !cancellationToken.CanBeCanceled)
        {
            return null;
        }

        return p =>
        {
            if (p.Total > 0)
            {
                progress?.Report((double)p.Current / p.Total);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                p.Cancel = true;
            }
        };
    }

    private sealed class MountedWim(string mountDirectory) : IMountedImage
    {
        private bool _unmounted;

        public string MountDirectory => mountDirectory;

        public Task UnmountAsync(bool commit, CancellationToken cancellationToken)
        {
            return Task.Run(
                () =>
                {
                    try
                    {
                        MsDism.DismApi.UnmountImage(mountDirectory, commit, WrapProgress(null, cancellationToken));
                        _unmounted = true;
                    }
                    catch (MsDism.DismException ex)
                    {
                        throw new BootrixException(ErrorCode.ImageMountFailed, $"unmount {mountDirectory}: {ex.Message}", ex);
                    }
                },
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_unmounted)
            {
                return;
            }

            // Reached after a failure or a cancel: throw the changes away so no half-edited image stays mounted.
            await Task.Run(() =>
            {
                try
                {
                    MsDism.DismApi.UnmountImage(mountDirectory, false);
                }
                catch (MsDism.DismException)
                {
                    MsDism.DismApi.CleanupMountpoints();
                }
            }).ConfigureAwait(false);
            _unmounted = true;
        }
    }
}
