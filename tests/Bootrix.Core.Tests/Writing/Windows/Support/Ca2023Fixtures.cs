// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Boot;
using Bootrix.Core.Tests.Boot;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows.Support;

/// <summary>
/// Synthetic Windows media for the boot manager swap. A signature of Microsoft's CA cannot be forged, so the tests
/// state the signature facts through markers inside the files, which the stand-in analyser turns into reports.
/// </summary>
internal static class Ca2023Fixtures
{
    public const string New2023 = "EX2023";
    public const string Old2011 = "OLD2011";

    /// <summary>A structurally valid EFI application whose first section starts with the marker.</summary>
    public static byte[] Efi(string marker, ushort machine = 0x8664, string note = "")
    {
        var data = new byte[0x300];
        Encoding.ASCII.GetBytes(marker + ":" + note).CopyTo(data, 0);
        return new PeBuilder { Machine = machine }.AddSection(".text", data).Build();
    }

    /// <summary>The folders of boot.wim that the swap extracts, as wimlib puts them.</summary>
    public static void WriteExtracted(string root, bool bootManager = true, bool fonts = true, ushort machine = 0x8664)
    {
        var efi = Path.Combine(root, "EFI_EX");
        Directory.CreateDirectory(efi);
        if (bootManager)
        {
            File.WriteAllBytes(Path.Combine(efi, "bootmgfw_EX.efi"), Efi(New2023, machine, "bootmgfw"));
            File.WriteAllBytes(Path.Combine(efi, "bootmgr_EX.efi"), Efi(New2023, machine, "bootmgr"));
            File.WriteAllBytes(Path.Combine(efi, "cdboot_EX.efi"), Efi(New2023, machine, "cdboot"));
        }

        if (fonts)
        {
            var folder = Path.Combine(root, "Fonts_EX");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "segoe_slboot_EX.ttf"), "new segoe");
            File.WriteAllText(Path.Combine(folder, "wgl4_boot_EX.ttf"), "new wgl4");
            File.WriteAllText(Path.Combine(folder, "chs_boot_EX.ttf"), "new chs");
        }
    }

    /// <summary>What the writer leaves on a Windows medium; <paramref name="upper"/> mimics the casing of an ISO.</summary>
    public static void WriteMedia(ScratchFolder media, bool upper = false, ushort machine = 0x8664, bool withBootManagerCopy = true, bool withBootWim = true)
    {
        string Name(string path) => upper ? path.ToUpperInvariant() : path;

        media.Write(Name("efi/boot/bootx64.efi"), Efi(Old2011, machine, "fallback"));
        if (withBootManagerCopy)
        {
            media.Write(Name("efi/microsoft/boot/bootmgfw.efi"), Efi(Old2011, machine, "bootmgfw copy"));
        }

        media.Write(Name("efi/microsoft/boot/cdboot.efi"), Efi(Old2011, machine, "cdboot"));
        media.Write(Name("efi/microsoft/boot/memtest.efi"), Efi(Old2011, machine, "memtest kept"));
        media.Write(Name("efi/microsoft/boot/bcd"), "BCD");
        media.Write(Name("efi/microsoft/boot/fonts/segoe_slboot.ttf"), "old segoe");
        media.Write(Name("efi/microsoft/boot/fonts/wgl4_boot.ttf"), "old wgl4");
        media.Write(Name("bootmgr.efi"), Efi(Old2011, machine, "bootmgr"));
        media.Write(Name("bootmgr"), "bootmgr");
        if (withBootWim)
        {
            media.Write(Name("sources/boot.wim"), FakeWims.BootWim());
        }
    }

    public static string Snapshot(string root) =>
        string.Join(
            '\n',
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(root, file) + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));

    /// <summary>Copies a prepared tree to the destination, the way wimlib would extract it.</summary>
    public sealed class FolderExtractor(string prepared) : IBootFileExtractor
    {
        public List<(string Image, int Index, string[] Paths)> Calls { get; } = [];

        public Task ExtractAsync(string imagePath, int imageIndex, IReadOnlyList<string> imagePaths, string destination, CancellationToken cancellationToken)
        {
            Calls.Add((imagePath, imageIndex, [.. imagePaths]));
            foreach (var file in Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(prepared, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Reports what the markers say: 2023 for the new files, 2011 for the old ones, nothing for "UNSIGNED". The
    /// streams are checked to be open while the analyser runs, which is when the real one reads them.
    /// </summary>
    public sealed class MarkerAnalyzer
    {
        public List<string> Labels { get; } = [];

        public List<Stream> Streams { get; } = [];

        public Action? OnAnalyze { get; set; }

        public EfiAnalysisReport Analyze(IReadOnlyList<(string Path, Stream Data)> files, CancellationToken cancellationToken)
        {
            OnAnalyze?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var reports = new List<EfiFileReport>();
            foreach (var (path, data) in files)
            {
                Labels.Add(path);
                Streams.Add(data);
                using var buffer = new MemoryStream();
                data.CopyTo(buffer);
                var text = Encoding.ASCII.GetString(buffer.ToArray());
                var signatures = new List<EfiSignature>();
                if (text.Contains(New2023, StringComparison.Ordinal))
                {
                    signatures.Add(CompatibilityMatrixTests.Sig(SignatureAuthority.WindowsUefiCa2023));
                }
                else if (text.Contains(Old2011, StringComparison.Ordinal))
                {
                    signatures.Add(CompatibilityMatrixTests.Sig(SignatureAuthority.WindowsProductionPca2011));
                }

                reports.Add(CompatibilityMatrixTests.Report(path, EfiMachine.X64, EfiFileAnalyzer.DetermineRole(path), signatures));
            }

            return new EfiMediaAnalyzer().Summarize(reports);
        }
    }
}
