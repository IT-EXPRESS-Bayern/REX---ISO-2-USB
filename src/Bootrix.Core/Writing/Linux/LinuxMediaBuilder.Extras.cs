// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Boot;
using Bootrix.Core.Images;
using Bootrix.Core.Writing.Linux.Patching;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Writing.Linux;

public sealed partial class LinuxMediaBuilder
{
    private sealed partial class Run
    {
        private static readonly string[] NativeSyslinuxConfigs = ["syslinux.cfg", "boot/syslinux/syslinux.cfg", "syslinux/syslinux.cfg"];
        private static readonly string[] CompanionModules = ["libcom32.c32", "libutil.c32"];

        private bool IsInSyslinuxTree(string path)
        {
            var directory = path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";
            return directory.Length == 0 || directory.Equals(_facts.SyslinuxDirectory, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Syslinux looks for ldlinux.c32 and its configuration in the root directory and in /boot/syslinux and /syslinux.
        /// Images that keep both in /isolinux therefore get a copy of the module in the root and a small syslinux.cfg
        /// that hands over to the image's own configuration (the same approach Rufus takes).
        /// </summary>
        private void AddSyslinuxFiles()
        {
            if (_syslinux is null)
            {
                return;
            }

            if (!_taken.Contains("ldlinux.c32"))
            {
                var fromImage = !_replaceModules && _facts.LdlinuxModule is not null;
                WriteGenerated("ldlinux.c32", fromImage ? ReadAll(_facts.LdlinuxModule!) : _syslinux.LdlinuxModule.ToArray());
            }

            if (_replaceModules)
            {
                AddMissingModules();
            }

            if (_facts.SyslinuxConfig is { } config && !NativeSyslinuxConfigs.Contains(config, StringComparer.OrdinalIgnoreCase) && !_taken.Contains("syslinux.cfg"))
            {
                var directory = _facts.SyslinuxDirectory;
                var prefix = directory.Length == 0 ? "/" : "/" + directory + "/";
                WriteGenerated("syslinux.cfg", Encoding.ASCII.GetBytes($"DEFAULT loadconfig\n\nLABEL loadconfig\n  CONFIG /{config}\n  APPEND {prefix}\n"));
            }
        }

        /// <summary>A menu from another Syslinux generation needs the support libraries of the replacement modules next to it.</summary>
        private void AddMissingModules()
        {
            var replaced = _written.Count(file => file.Source == WrittenFileSource.Generated && file.Path.EndsWith(".c32", StringComparison.OrdinalIgnoreCase));
            var directory = _facts.SyslinuxDirectory;
            foreach (var name in CompanionModules)
            {
                var path = directory.Length == 0 ? name : directory + "/" + name;
                if (_taken.Add(path) && _syslinux!.TryGetModule(name, out var module))
                {
                    WriteGenerated(path, module.ToArray(), alreadyTaken: true);
                    replaced++;
                }
            }

            _notices.Add(new ImageWarning(LinuxNoticeKeys.SyslinuxModulesReplaced, WarningSeverity.Warning, replaced));
        }

        /// <summary>The GRUB that Bootrix installs reads /boot/grub/grub.cfg; an image with its menu in /boot/grub2 gets a file that points there.</summary>
        private void AddGrubMenuStub()
        {
            if (_settings.Bios.Loader != BiosLoader.Grub || _facts.HasGrubConfig || _facts.Grub2Config is not { } relocated)
            {
                return;
            }

            _notices.Add(new ImageWarning(LinuxNoticeKeys.GrubMenuRelocated, WarningSeverity.Info));
            WriteGenerated("boot/grub/grub.cfg", Encoding.ASCII.GetBytes($"configfile /{relocated}\n"));
        }

        /// <summary>
        /// Some images (Solus and others) carry their EFI loaders only inside the FAT image of the El Torito catalog, and
        /// others ship a bootx64.efi that is a dangling symbolic link. Both are repaired from that image.
        /// </summary>
        private void CopyEfiBootImage()
        {
            if (!_settings.Uefi || (_facts.EfiLoaders.Count > 0 && !_facts.HasBrokenFallbackLoader))
            {
                return;
            }

            var image = _iso.EfiImage();
            if (image is null)
            {
                return;
            }

            var copied = 0;
            foreach (var file in image.Files)
            {
                var broken = _written.FindIndex(w => w.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && w.Length < 256);
                if (broken < 0 && !_taken.Add(file.Path))
                {
                    continue;
                }

                if (broken >= 0)
                {
                    _written.RemoveAt(broken);
                }

                using var source = image.OpenFile(file.Path);
                var content = new byte[file.Length];
                source.ReadExactly(content);
                WriteGenerated(file.Path, content, alreadyTaken: true);
                copied++;
            }

            if (copied > 0)
            {
                _notices.Add(new ImageWarning(LinuxNoticeKeys.EfiLoadersFromBootImage, WarningSeverity.Info));
            }
        }

        private void WriteChecksumList()
        {
            if (_facts.ChecksumList is not { } list)
            {
                return;
            }

            var original = ReadAll(list);
            var text = Latin1.GetString(original);
            var updated = _patchedChecksums.Count == 0 ? text : Md5SumFile.Update(text, _patchedChecksums);
            if (string.Equals(text, updated, StringComparison.Ordinal))
            {
                _taken.Add(list);
                WriteBytes(list, original, WrittenFileSource.Image, keepContent: false);
                return;
            }

            var lines = text.Split('\n').Zip(updated.Split('\n')).Count(pair => pair.First != pair.Second);
            _notices.Add(new ImageWarning(LinuxNoticeKeys.ChecksumsUpdated, WarningSeverity.Info, lines));
            _taken.Add(list);
            WriteBytes(list, Latin1.GetBytes(updated), WrittenFileSource.Patched);
        }

        private EfiAnalysisReport? ReviewEfiLoaders()
        {
            var report = _owner._efi.Analyze(EfiStreams());
            foreach (var message in report.Summary.Concat(report.Recommendations))
            {
                var severity = report.Verdict is EfiMediaVerdict.Revoked or EfiMediaVerdict.NoSignature or EfiMediaVerdict.InvalidSignature
                    && report.Summary.Contains(message) ? WarningSeverity.Warning : WarningSeverity.Info;
                _notices.Add(new ImageWarning(message.Key, severity, message.Arguments));
                _owner._log.LogInformation("{Message}", message.Format());
            }

            return report;
        }

        /// <summary>The loaders of the image, opened one at a time; the analyzer reads each to the end before asking for the next.</summary>
        private IEnumerable<(string Path, Stream Data)> EfiStreams()
        {
            foreach (var loader in _facts.EfiLoaders.Where(l => l.Length >= 256))
            {
                using var stream = _iso.OpenFile(loader.Path);
                yield return (loader.Path, stream);
            }

            if (_facts.EfiLoaders.Count > 0 && !_facts.HasBrokenFallbackLoader)
            {
                yield break;
            }

            var image = _iso.EfiImage();
            foreach (var file in image?.Files.Where(f => f.Length >= 256 && f.Path.EndsWith(".efi", StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                using var stream = image!.OpenFile(file.Path);
                yield return (file.Path, stream);
            }
        }

        private byte[] ReadAll(string path)
        {
            using var source = _iso.OpenFile(path);
            var bytes = new byte[source.Length];
            source.ReadExactly(bytes);
            return bytes;
        }

        private void WriteGenerated(string path, byte[] content, bool alreadyTaken = false)
        {
            if (!alreadyTaken)
            {
                _taken.Add(path);
            }

            WriteBytes(path, content, WrittenFileSource.Generated);
        }
    }
}
