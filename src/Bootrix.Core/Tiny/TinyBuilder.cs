// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Unattend;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Tiny;

/// <summary>
/// Builds a slimmed-down Windows installation medium from an original one. The builder only
/// decides what happens and in which order; every operation on the image goes through the
/// service interfaces, which keeps the order testable and guarantees that an aborted build
/// unloads its registry hives and discards the mounted image.
/// </summary>
public sealed class TinyBuilder
{
    public const string InstallImageName = "install.wim";

    private readonly IImageServicing _servicing;
    private readonly IImageFileSystem _files;
    private readonly IInstallImageTools _tools;
    private readonly IIsoWriter? _isoWriter;

    public TinyBuilder(IImageServicing servicing, IImageFileSystem files, IInstallImageTools tools, IIsoWriter? isoWriter = null)
    {
        _servicing = servicing;
        _files = files;
        _tools = tools;
        _isoWriter = isoWriter;
    }

    /// <summary>The profile with the groups the user switched off already filtered out.</summary>
    public static TinyProfile ResolveProfile(TinyBuildOptions options)
    {
        var profile = TinyProfiles.Load(options.ProfileId);
        bool On(string? group) => group is null || !options.DisabledGroups.Contains(group);

        return profile with
        {
            Appx = [.. profile.Appx.Where(a => On(a.Group))],
            Packages = [.. profile.Packages.Where(p => On(p.Group))],
            Capabilities = [.. profile.Capabilities.Where(c => On(c.Group))],
            Files = [.. profile.Files.Where(f => On(f.Group))],
            Registry = [.. profile.Registry.Where(r => On(r.Group))],
            BootWimRegistry = [.. profile.BootWimRegistry.Where(r => On(r.Group))],
            ScheduledTasks = On("telemetry") ? profile.ScheduledTasks : [],
            WinSxs = On("winsxs") ? profile.WinSxs : null,
            EmptyWinRe = profile.EmptyWinRe && On("winre"),
        };
    }

    public IJob CreateJob(TinyBuildOptions options)
    {
        var profile = ResolveProfile(options);
        if (profile.BreaksServicing && !options.AcknowledgeNoServicing)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "profile makes the image unserviceable")
            {
                Arguments = [$"{profile.Name} macht das Image nicht mehr wartbar; die Warnung muss bestätigt werden."],
            };
        }

        var run = new Run(this, options, profile);
        return new Job(
            $"tiny-{profile.Id}",
            profile.Name,
            [
                new DelegateJobStep("Tiny.Prepare", 2, run.PrepareAsync),
                new DelegateJobStep("Tiny.CopyMedia", 12, run.CopyMediaAsync),
                new DelegateJobStep("Tiny.ExportEdition", 14, run.ExportEditionAsync),
                new DelegateJobStep("Tiny.Mount", 5, run.MountAsync),
                new DelegateJobStep("Tiny.RemoveApps", 15, run.RemoveAppsAsync),
                new DelegateJobStep("Tiny.RemoveComponents", 10, run.RemoveComponentsAsync),
                new DelegateJobStep("Tiny.RemoveFiles", 6, run.RemoveFilesAsync),
                new DelegateJobStep("Tiny.Registry", 5, run.ApplyRegistryAsync),
                new DelegateJobStep("Tiny.CleanupStore", 8, run.CleanupStoreAsync),
                new DelegateJobStep("Tiny.Unmount", 8, run.UnmountAsync),
                new DelegateJobStep("Tiny.Compress", 14, run.CompressAsync),
                new DelegateJobStep("Tiny.PatchBootImage", 5, run.PatchBootImageAsync),
                new DelegateJobStep("Tiny.Unattend", 1, run.WriteUnattendAsync),
                new DelegateJobStep("Tiny.CreateIso", 8, run.CreateIsoAsync),
            ]);
    }

    private sealed class Run(TinyBuilder owner, TinyBuildOptions options, TinyProfile profile)
    {
        private string _media = "";
        private string _mount = "";
        private string _architecture = "amd64";
        private IMountedImage? _image;
        private bool _committed;
        private bool _winSxsRebuilt;

        private string InstallImage => Path.Combine(_media, "sources", InstallImageName);

        private string BootImage => Path.Combine(_media, "sources", "boot.wim");

        public Task PrepareAsync(JobContext context, CancellationToken cancellationToken)
        {
            _media = Path.Combine(options.WorkDirectory, "media");
            _mount = Path.Combine(options.WorkDirectory, "mount");
            Directory.CreateDirectory(_media);
            Directory.CreateDirectory(_mount);

            var source = FindSourceImage();
            var needed = new FileInfo(source).Length * 3 + (2L << 30);
            var free = owner._files.GetFreeBytes(options.WorkDirectory);
            if (free < needed)
            {
                throw new BootrixException(ErrorCode.InsufficientSpace, options.WorkDirectory)
                {
                    Arguments = [options.WorkDirectory, FormatGigabytes(needed)],
                };
            }

            if (!options.KeepWorkDirectory)
            {
                context.OnCleanup(() => TryDeleteDirectory(_mount));
            }

            return Task.CompletedTask;
        }

        public Task CopyMediaAsync(JobContext context, CancellationToken cancellationToken)
        {
            // The install image is exported edition by edition in the next step, so the original is not copied.
            return owner._files.CopyDirectoryAsync(
                options.SourceRoot,
                _media,
                path => !IsInstallImage(path),
                new Progress<double>(value => context.ReportStep(value)),
                cancellationToken);
        }

        public async Task ExportEditionAsync(JobContext context, CancellationToken cancellationToken)
        {
            var source = FindSourceImage();
            await owner._tools.ExportEditionAsync(
                source,
                options.ImageIndex,
                InstallImage,
                InstallImageCompression.Maximum,
                new Progress<double>(value => context.ReportStep(value)),
                cancellationToken).ConfigureAwait(false);
            _architecture = await owner._tools.GetArchitectureAsync(InstallImage, 1, cancellationToken).ConfigureAwait(false);
            context.Log.LogInformation("Edition {Index} exported, architecture {Architecture}", options.ImageIndex, _architecture);
        }

        public async Task MountAsync(JobContext context, CancellationToken cancellationToken)
        {
            _image = await owner._servicing.MountAsync(
                InstallImage,
                1,
                _mount,
                new Progress<double>(value => context.ReportStep(value)),
                cancellationToken).ConfigureAwait(false);

            // If anything below fails, the changes are thrown away instead of leaving a half-modified image mounted.
            context.OnCleanup(async () =>
            {
                if (_image is not null && !_committed)
                {
                    await _image.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        public async Task RemoveAppsAsync(JobContext context, CancellationToken cancellationToken)
        {
            var installed = await owner._servicing.GetProvisionedAppxAsync(_mount, cancellationToken).ConfigureAwait(false);
            var targets = installed
                .Where(app => profile.Appx.Any(r => app.PackageName.Contains(r.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            for (var i = 0; i < targets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await owner._servicing.RemoveProvisionedAppxAsync(_mount, targets[i].PackageName, cancellationToken).ConfigureAwait(false);
                context.ReportStep((i + 1.0) / targets.Count, detail: targets[i].DisplayName);
            }
        }

        public async Task RemoveComponentsAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (profile.Packages.Count > 0)
            {
                var language = await owner._tools.GetDefaultLanguageAsync(InstallImage, 1, cancellationToken).ConfigureAwait(false);
                var patterns = profile.Packages.Select(p => p.Pattern.Replace("{lang}", language, StringComparison.Ordinal)).ToList();
                var packages = await owner._servicing.GetPackagesAsync(_mount, cancellationToken).ConfigureAwait(false);
                foreach (var package in packages.Where(p => patterns.Any(x => p.Identity.StartsWith(x, StringComparison.OrdinalIgnoreCase))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await owner._servicing.RemovePackageAsync(_mount, package.Identity, cancellationToken).ConfigureAwait(false);
                }
            }

            if (profile.Capabilities.Count > 0)
            {
                var capabilities = await owner._servicing.GetCapabilitiesAsync(_mount, cancellationToken).ConfigureAwait(false);
                var targets = capabilities.Where(c => c.State.Equals("Installed", StringComparison.OrdinalIgnoreCase)
                    && profile.Capabilities.Any(p => c.Name.StartsWith(p.Pattern, StringComparison.OrdinalIgnoreCase)));
                foreach (var capability in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await owner._servicing.RemoveCapabilityAsync(_mount, capability.Name, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async Task RemoveFilesAsync(JobContext context, CancellationToken cancellationToken)
        {
            foreach (var file in profile.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await owner._files.DeleteAsync(InImage(file.Path), file.TakeOwnership, cancellationToken).ConfigureAwait(false);
            }

            if (profile.EmptyWinRe)
            {
                await owner._files.ReplaceWithEmptyFileAsync(Path.Combine(_mount, "Windows", "System32", "Recovery", "winre.wim"), cancellationToken).ConfigureAwait(false);
            }

            if (profile.WinSxs is { } winSxs)
            {
                if (winSxs.KeepByArchitecture.TryGetValue(_architecture, out var keep))
                {
                    await owner._files.RebuildWinSxsAsync(Path.Combine(_mount, "Windows", "WinSxS"), keep, cancellationToken).ConfigureAwait(false);
                    _winSxsRebuilt = true;
                }
                else
                {
                    // Without a list of what to keep for this architecture the component store would be wiped completely.
                    context.Log.LogWarning("No WinSxS whitelist for {Architecture}; the component store is left alone", _architecture);
                }
            }

            foreach (var task in profile.ScheduledTasks)
            {
                await owner._files.DeleteAsync(InImage(Path.Combine("Windows", "System32", "Tasks", task)), takeOwnership: true, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task ApplyRegistryAsync(JobContext context, CancellationToken cancellationToken)
        {
            ApplyRegistry(_mount, profile.Registry, cancellationToken);
            return Task.CompletedTask;
        }

        public async Task CleanupStoreAsync(JobContext context, CancellationToken cancellationToken)
        {
            // With the component store rebuilt there is nothing left that cleanup could work on.
            if (!_winSxsRebuilt)
            {
                await owner._servicing.CleanupComponentStoreAsync(_mount, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task UnmountAsync(JobContext context, CancellationToken cancellationToken)
        {
            await _image!.UnmountAsync(commit: true, cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public async Task CompressAsync(JobContext context, CancellationToken cancellationToken)
        {
            var temporary = InstallImage + ".tmp";
            await owner._tools.ExportEditionAsync(
                InstallImage,
                1,
                temporary,
                options.Compression,
                new Progress<double>(value => context.ReportStep(value)),
                cancellationToken).ConfigureAwait(false);

            File.Delete(InstallImage);
            File.Move(temporary, InstallImage);
        }

        public async Task PatchBootImageAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (!options.BypassHardwareChecks || profile.BootWimRegistry.Count == 0 || !File.Exists(BootImage))
            {
                return;
            }

            var bootMount = Path.Combine(options.WorkDirectory, "mount-boot");
            Directory.CreateDirectory(bootMount);

            // Index 2 is the Windows Setup environment; index 1 is plain WinPE and stays untouched.
            await using var image = await owner._servicing.MountAsync(BootImage, 2, bootMount, null, cancellationToken).ConfigureAwait(false);
            ApplyRegistry(bootMount, profile.BootWimRegistry, cancellationToken);
            await image.UnmountAsync(commit: true, cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(bootMount);
        }

        public Task WriteUnattendAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (options.Unattend is { } unattend)
            {
                // The exported install image holds exactly one edition, and its architecture is only known after the export.
                var matched = unattend with { Arch = ArchitectureOf(_architecture), ImageIndex = 1, ImageName = null };

                // The answer file belongs at the root of the media; Setup does not read the copy in Sysprep for new installations.
                File.WriteAllBytes(Path.Combine(_media, "autounattend.xml"), UnattendBuilder.ToBytes(matched));
            }

            return Task.CompletedTask;
        }

        private static WindowsArch ArchitectureOf(string architecture) => architecture.ToLowerInvariant() switch
        {
            "arm64" => WindowsArch.Arm64,
            "x86" => WindowsArch.X86,
            _ => WindowsArch.X64,
        };

        public async Task CreateIsoAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (options.IsoPath is null)
            {
                context.Set("tiny.media", _media);
                return;
            }

            var writer = owner._isoWriter
                ?? throw new BootrixException(ErrorCode.ExternalToolFailed, "no ISO writer available") { Arguments = ["oscdimg", "not available"] };
            await writer.CreateAsync(
                _media,
                options.IsoPath,
                options.VolumeLabel,
                uefi2023: false,
                new Progress<double>(value => context.ReportStep(value)),
                cancellationToken).ConfigureAwait(false);
            context.Set("tiny.media", _media);
            context.Set("tiny.iso", options.IsoPath);
        }

        private void ApplyRegistry(string mountDirectory, IReadOnlyList<RegistryChange> changes, CancellationToken cancellationToken) =>
            RegistryChangeApplier.Apply(owner._files, mountDirectory, changes, cancellationToken);

        private string FindSourceImage()
        {
            foreach (var name in new[] { "install.wim", "install.esd", "install.swm" })
            {
                var path = Path.Combine(options.SourceRoot, "sources", name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new BootrixException(ErrorCode.ImageUnsupported, "no install image in " + options.SourceRoot) { Arguments = ["sources\\install.wim"] };
        }

        private bool IsInstallImage(string path)
        {
            var relative = Path.GetRelativePath(options.SourceRoot, path).Replace('/', '\\');
            return relative.StartsWith("sources\\install.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Profile paths use backslashes; convert them for the platform the builder runs on.</summary>
        private string InImage(string relative) => Path.Combine(_mount, relative.Replace('\\', Path.DirectorySeparatorChar));

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover empty mount folder is harmless.
            }
        }

        private static string FormatGigabytes(long bytes) => $"{bytes / (double)(1L << 30):0.#} GB";
    }
}
