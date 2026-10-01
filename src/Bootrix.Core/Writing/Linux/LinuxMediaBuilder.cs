// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Boot;
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Writing.Linux.Patching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// Fills the main partition of a Linux medium: copies the files of the image, adapts the boot configuration to the
/// label and the persistence store of the medium, adds what the chosen BIOS loader needs next to them and checks the
/// EFI loaders. Everything goes through <see cref="ITargetVolume"/>, so it runs unchanged on a mounted stick and on a folder.
/// </summary>
public sealed partial class LinuxMediaBuilder(ILogger<LinuxMediaBuilder>? logger = null, EfiMediaAnalyzer? efiAnalyzer = null)
{
    private const int MaxConfigBytes = 1024 * 1024;
    private const int CopyBufferBytes = 1024 * 1024;
    private const int ReportEveryBytes = 4 * 1024 * 1024;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly ILogger _log = logger ?? NullLogger<LinuxMediaBuilder>.Instance;
    private readonly EfiMediaAnalyzer _efi = efiAnalyzer ?? new EfiMediaAnalyzer();

    /// <exception cref="BootrixException">A file cannot be read from the image or written to the medium.</exception>
    public LinuxBuildResult Build(
        IsoContent iso,
        ITargetVolume volume,
        LinuxBuildSettings settings,
        IProgress<LinuxCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(settings);

        var run = new Run(this, iso, volume, settings, LinuxTreeFacts.Scan(iso), progress, cancellationToken);
        return run.Execute();
    }

    /// <summary>The state of one build, so that the steps can share what they have written.</summary>
    private sealed partial class Run
    {
        private readonly LinuxMediaBuilder _owner;
        private readonly IsoContent _iso;
        private readonly ITargetVolume _volume;
        private readonly LinuxBuildSettings _settings;
        private readonly LinuxTreeFacts _facts;
        private readonly IProgress<LinuxCopyProgress>? _progress;
        private readonly CancellationToken _cancellationToken;

        private readonly List<WrittenFile> _written = [];
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PatchedConfig> _patches = [];
        private readonly List<ImageWarning> _notices = [];
        private readonly Dictionary<string, string> _patchedChecksums = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConfigPatchOptions _patchOptions;
        private readonly SyslinuxBundle? _syslinux;
        private readonly bool _replaceModules;
        private long _copied;
        private long _reported;

        public Run(LinuxMediaBuilder owner, IsoContent iso, ITargetVolume volume, LinuxBuildSettings settings, LinuxTreeFacts facts, IProgress<LinuxCopyProgress>? progress, CancellationToken cancellationToken)
        {
            _owner = owner;
            _iso = iso;
            _volume = volume;
            _settings = settings;
            _facts = facts;
            _progress = progress;
            _cancellationToken = cancellationToken;

            _syslinux = settings.Bios.Loader == BiosLoader.Syslinux ? settings.Bios.Syslinux?.Bundle : null;
            _replaceModules = settings.Bios.Syslinux?.Match == SyslinuxMatch.Different && _syslinux is not null;
            _patchOptions = new ConfigPatchOptions
            {
                OldLabel = Latin1Of(iso.VolumeLabel),
                NewLabel = Latin1Of(settings.MediumLabel),
                Persistence = PersistenceStyleOf(settings),
                EsxiFirstPartition = string.Equals(settings.Family, "esxi", StringComparison.OrdinalIgnoreCase),
            };
        }

        public LinuxBuildResult Execute()
        {
            _notices.AddRange(_settings.Bios.Notices);
            ExplainPersistence();

            var total = _iso.TotalBytes;
            WriteLdlinuxPlaceholder();
            CopyDirectories();
            foreach (var file in _iso.Files)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                CopyFile(file, total);
            }

            AddSyslinuxFiles();
            AddGrubMenuStub();
            CopyEfiBootImage();
            WriteChecksumList();
            SummarizePatches();

            var efi = _settings.Uefi ? ReviewEfiLoaders() : null;
            _progress?.Report(new LinuxCopyProgress(total, total, ""));
            return new LinuxBuildResult(_settings.Bios, _written, _patches, _notices, efi);
        }

        private static string? Latin1Of(string? text) =>
            string.IsNullOrEmpty(text) ? null : Latin1.GetString(Encoding.UTF8.GetBytes(text));

        private static PersistenceStyle PersistenceStyleOf(LinuxBuildSettings settings)
        {
            if (!settings.Persistence)
            {
                return PersistenceStyle.None;
            }

            return settings.Traits.Casper ? PersistenceStyle.Casper : settings.Traits.LiveBoot ? PersistenceStyle.LiveBoot : PersistenceStyle.None;
        }

        private bool PatchesConfigs => _patchOptions.Persistence != PersistenceStyle.None
            || _patchOptions.EsxiFirstPartition
            || (_patchOptions.OldLabel is not null && _patchOptions.NewLabel is not null && _patchOptions.OldLabel != _patchOptions.NewLabel);

        private void ExplainPersistence()
        {
            if (_settings.Persistence && _patchOptions.Persistence == PersistenceStyle.None)
            {
                _notices.Add(new ImageWarning(LinuxNoticeKeys.PersistenceNotPatched, WarningSeverity.Warning, _settings.Family ?? "-"));
            }
        }

        /// <summary>
        /// ldlinux.sys goes on the empty volume first, so that the file system lays it out in one piece; the installer
        /// finds it by name afterwards and writes the sector map into the sectors it occupies.
        /// </summary>
        private void WriteLdlinuxPlaceholder()
        {
            if (_syslinux is null)
            {
                return;
            }

            var content = SyslinuxInstaller.CreateLdlinuxFile(_syslinux);
            using (var stream = _volume.CreateFile(SyslinuxInstaller.LdlinuxFileName, content.Length))
            {
                stream.Write(content);
            }

            _volume.SetAttributes(SyslinuxInstaller.LdlinuxFileName, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            _taken.Add(SyslinuxInstaller.LdlinuxFileName);

            // Listed without content: the installer rewrites it, so a read-back comparison has nothing to compare with.
            _written.Add(new WrittenFile(SyslinuxInstaller.LdlinuxFileName, content.Length, WrittenFileSource.Generated));
        }

        private void CopyDirectories()
        {
            foreach (var directory in _iso.Directories)
            {
                try
                {
                    _volume.CreateDirectory(directory);
                }
                catch (ArgumentException ex)
                {
                    Skip(directory, ex.Message);
                }
            }
        }

        private void CopyFile(IsoFile file, long total)
        {
            var path = file.Path;
            if (IsSyslinuxOwn(path) || (_facts.ChecksumList is not null && path.Equals(_facts.ChecksumList, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (!_taken.Add(path))
            {
                Skip(path, "a name that differs only in case is already on the medium");
                return;
            }

            try
            {
                if (PatchesConfigs && IsConfigCandidate(file))
                {
                    CopyConfig(file);
                }
                else if (_replaceModules && _syslinux!.TryGetModule(Path.GetFileName(path), out var module) && IsInSyslinuxTree(path))
                {
                    WriteBytes(path, module.ToArray(), WrittenFileSource.Generated);
                }
                else
                {
                    CopyStream(file, total);
                }
            }
            catch (ArgumentException ex)
            {
                _taken.Remove(path);
                Skip(path, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new BootrixException(ErrorCode.FileCopyFailed, $"{path}: {ex.Message}", ex) { Arguments = [path, ex.Message] };
            }
        }

        /// <summary>ldlinux.sys and ldlinux.bss of the image are those of another installation; only the one Bootrix writes may stay.</summary>
        private bool IsSyslinuxOwn(string path) =>
            _syslinux is not null && (path.Equals("ldlinux.sys", StringComparison.OrdinalIgnoreCase) || path.Equals("ldlinux.bss", StringComparison.OrdinalIgnoreCase));

        private static bool IsConfigCandidate(IsoFile file)
        {
            var extension = Path.GetExtension(file.Path);
            return file.Length is > 0 and <= MaxConfigBytes
                && (extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".conf", StringComparison.OrdinalIgnoreCase))
                && !file.Path.StartsWith("pool/", StringComparison.OrdinalIgnoreCase)
                && !file.Path.StartsWith("dists/", StringComparison.OrdinalIgnoreCase);
        }

        [SuppressMessage("Security", "CA5351", Justification = "md5sum.txt lists MD5 values of the files; the checksum is only kept in step with the patched file, nothing is authenticated with it.")]
        private void CopyConfig(IsoFile file)
        {
            byte[] bytes;
            using (var source = _iso.OpenFile(file.Path))
            {
                bytes = new byte[file.Length];
                source.ReadExactly(bytes);
            }

            var text = Latin1.GetString(bytes);
            var result = BootConfigPatcher.Patch(text, _patchOptions);
            if (!result.Changed || bytes.AsSpan().Contains((byte)0))
            {
                WriteBytes(file.Path, bytes, WrittenFileSource.Image, keepContent: false);
                return;
            }

            var patched = Latin1.GetBytes(result.Text);
            _patches.Add(new PatchedConfig(file.Path, result.Changes));
            _patchedChecksums[file.Path] = Convert.ToHexString(MD5.HashData(patched)).ToLowerInvariant();
            WriteBytes(file.Path, patched, WrittenFileSource.Patched);
        }

        private void CopyStream(IsoFile file, long total)
        {
            using var source = _iso.OpenFile(file.Path);
            using var target = _volume.CreateFile(file.Path, file.Length);
            var buffer = new byte[CopyBufferBytes];
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                _copied += read;
                if (_copied - _reported >= ReportEveryBytes)
                {
                    _reported = _copied;
                    _progress?.Report(new LinuxCopyProgress(_copied, total, file.Path));
                }
            }

            _written.Add(new WrittenFile(file.Path, file.Length, WrittenFileSource.Image));
        }

        private void WriteBytes(string path, byte[] content, WrittenFileSource source, bool keepContent = true)
        {
            using (var target = _volume.CreateFile(path, content.Length))
            {
                target.Write(content);
            }

            _copied += content.Length;
            _written.Add(new WrittenFile(path, content.Length, source, keepContent ? content : null));
        }

        private void Skip(string path, string reason)
        {
            _owner._log.LogWarning("Skipped {Path}: {Reason}", path, reason);
            _notices.Add(new ImageWarning(LinuxNoticeKeys.FileSkipped, WarningSeverity.Warning, path, reason));
        }

        private void SummarizePatches()
        {
            var labelLines = _patches.SelectMany(p => p.Changes).Count(c => c.Kind == ConfigChangeKind.Label);
            if (labelLines > 0)
            {
                _notices.Add(new ImageWarning(LinuxNoticeKeys.LabelRewritten, WarningSeverity.Info, _iso.VolumeLabel, _settings.MediumLabel, labelLines));
            }

            if (_patchOptions.Persistence != PersistenceStyle.None)
            {
                var parameter = _patchOptions.Persistence == PersistenceStyle.Casper ? "persistent" : "persistence";
                var added = _patches.SelectMany(p => p.Changes).Count(c => c.Kind == ConfigChangeKind.PersistenceParameter);
                _notices.Add(added > 0
                    ? new ImageWarning(LinuxNoticeKeys.PersistenceParameterAdded, WarningSeverity.Info, parameter, added)
                    : new ImageWarning(LinuxNoticeKeys.PersistenceNoBootLine, WarningSeverity.Warning, parameter));
            }

            if (_patches.SelectMany(p => p.Changes).Any(c => c.Kind == ConfigChangeKind.EsxiPartition))
            {
                _notices.Add(new ImageWarning(LinuxNoticeKeys.EsxiPartitionAdded, WarningSeverity.Info));
            }
        }
    }
}
