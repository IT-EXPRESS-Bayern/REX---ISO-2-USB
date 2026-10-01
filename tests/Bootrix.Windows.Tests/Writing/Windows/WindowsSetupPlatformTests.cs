// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Images;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Writing.Windows;
using DiscUtils.Iso9660;

namespace Bootrix.Windows.Tests.Writing.Windows;

/// <summary>What only a real Windows can tell: how it shows an attached ISO, and which boot code its formatter writes.</summary>
public sealed class WindowsSetupPlatformTests : IDisposable
{
    private readonly WindowsWriteKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private string BuildIso(Dictionary<string, byte[]> files)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "WINTEST" };
        foreach (var (path, content) in files)
        {
            builder.AddFile(path.Replace('/', '\\'), content);
        }

        var iso = Path.Combine(_kit.Directory, "mounted.iso");
        builder.Build(iso);
        return iso;
    }

    [WindowsFact]
    public async Task AnAttachedIso_ShowsTheSameFilesAsTheImageStream()
    {
        var files = WindowsWriteKit.SetupFiles();
        var iso = BuildIso(files);
        var inspection = await new ImageInspector().InspectAsync(iso);

        using var mounted = VirtualDiskMounter.MountIso(iso);
        using var source = DirectoryMediaSource.Scan(mounted.RootPath!);

        Assert.True(WindowsSourceSession.ShowsTheSameFiles(source, inspection));
        foreach (var (path, content) in files)
        {
            await using var stream = source.OpenRead(path);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }
    }

    [WindowsFact]
    public async Task TheFormatterOfThisWindows_WritesBootCodeThatLoadsBootmgr()
    {
        if (!ProcessElevation.IsElevated())
        {
            // Attaching a writable virtual disk and formatting it needs the administrator token.
            return;
        }

        var code = await new ReferenceVolumeVbrSource().ReadFat32Async(Path.Combine(_kit.Directory, "bootcode"), CancellationToken.None);

        Assert.True(code.SectorCount >= 3);
        Assert.True(code.Sectors.AsSpan().IndexOf("BOOTMGR"u8) >= 0);
        Assert.Equal(0x55, code.Sectors[510]);
        Assert.Equal(0xAA, code.Sectors[511]);
    }
}
