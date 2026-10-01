// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Images;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Writing.Windows;
using Bootrix.Core.Errors;
using DiscUtils.Iso9660;
using Xunit.Abstractions;

namespace Bootrix.Windows.Tests.Writing.Windows;

/// <summary>What only a real Windows can tell: how it shows an attached ISO, and which boot code its formatter writes.</summary>
public sealed class WindowsSetupPlatformTests(ITestOutputHelper output) : IDisposable
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

        MountedImage mounted;
        try
        {
            mounted = VirtualDiskMounter.MountIso(iso);
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.ImageMountFailed)
        {
            // A runner without the virtual disk service cannot attach anything; that says nothing about the code under test.
            output.WriteLine("The ISO could not be attached on this machine: " + ex.Message);
            return;
        }

        using var attached = mounted;
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

        FatBootSectors code;
        try
        {
            code = await new ReferenceVolumeVbrSource().ReadFat32Async(Path.Combine(_kit.Directory, "bootcode"), CancellationToken.None);
        }
        catch (BootrixException ex) when (ex is { Code: ErrorCode.BootCodeUnavailable, InnerException: not null })
        {
            // Attaching or formatting the reference disk failed here. A boot code without BOOTMGR has no inner exception and fails the test.
            output.WriteLine("The reference volume could not be made on this machine: " + ex.InnerException);
            return;
        }

        Assert.True(code.SectorCount >= 3);
        Assert.True(code.Sectors.AsSpan().IndexOf("BOOTMGR"u8) >= 0);
        Assert.Equal(0x55, code.Sectors[510]);
        Assert.Equal(0xAA, code.Sectors[511]);
    }
}
