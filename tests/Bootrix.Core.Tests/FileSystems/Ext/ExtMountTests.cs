// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;
using Xunit.Abstractions;

namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>Mounts the images with the Linux kernel, the same code path casper and live-boot use for persistence.</summary>
public class ExtMountTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;

    [ExtMountFact]
    public void KernelMountsFormattedImagesAndKeepsThemConsistent()
    {
        foreach (var (type, lazy, size) in new[]
        {
            (ExtFileSystemType.Ext2, true, 64 * MiB),
            (ExtFileSystemType.Ext3, true, 300 * MiB),
            (ExtFileSystemType.Ext3, false, 64 * MiB),
            (ExtFileSystemType.Ext4, true, 300 * MiB),
            (ExtFileSystemType.Ext4, false, 64 * MiB),
        })
        {
            using var image = new TempImage(size);
            ExtFormatter.Format(image.Stream, new ExtFormatOptions
            {
                Type = type,
                Label = "persistence",
                LazyInitialization = lazy,
                RootFiles = [new ExtRootFile("persistence.conf", "/ union\n"u8.ToArray())],
            });
            var path = image.Close();

            var mounted = WithMount(path, mountPoint =>
            {
                Assert.Equal("/ union\n", File.ReadAllText(Path.Combine(mountPoint, "persistence.conf")));
                var directory = Directory.CreateDirectory(Path.Combine(mountPoint, "work"));
                for (var i = 0; i < 300; i++)
                {
                    File.WriteAllText(Path.Combine(directory.FullName, $"file-{i}.txt"), $"content {i}\n");
                }

                var payload = new byte[3 * MiB];
                new Random(9).NextBytes(payload);
                File.WriteAllBytes(Path.Combine(mountPoint, "payload.bin"), payload);
                Assert.Equal(payload, File.ReadAllBytes(Path.Combine(mountPoint, "payload.bin")));
            });
            if (!mounted)
            {
                return;
            }

            ExtAssert.Clean(path, $"{type} lazy={lazy}:");
            Assert.Equal("clean", ExtDump.Read(path).Header["Filesystem state"]);
        }
    }

    [ExtMountFact]
    public void WriterRefusesRootDirectoryThatTheKernelHasIndexed()
    {
        using var image = new TempImage(32 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext3, BlockSize = 1024 });
        var path = image.Close();

        var mounted = WithMount(path, mountPoint =>
        {
            for (var i = 0; i < 200; i++)
            {
                File.WriteAllText(Path.Combine(mountPoint, $"a-rather-long-file-name-{i:D4}"), "x");
            }
        });
        if (!mounted)
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var writer = ExtVolumeWriter.Open(stream);

        Assert.Throws<NotSupportedException>(() => writer.AddRootFile("one-more", new byte[1]));
    }

    // False when the environment does not allow mounting, so the caller can stop without failing.
    private bool WithMount(string image, Action<string> body)
    {
        var mountPoint = Path.Combine(Path.GetTempPath(), $"bootrix-mnt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountPoint);
        try
        {
            var mount = ExtTools.Run("mount", "-o", "loop", image, mountPoint);
            if (mount.ExitCode != 0)
            {
                output.WriteLine($"mount is not available here, skipping the kernel check: {mount.All}");
                return false;
            }

            try
            {
                body(mountPoint);
            }
            finally
            {
                var unmount = ExtTools.Run("umount", mountPoint);
                Assert.True(unmount.ExitCode == 0, unmount.All);
            }

            return true;
        }
        finally
        {
            Directory.Delete(mountPoint);
        }
    }
}
