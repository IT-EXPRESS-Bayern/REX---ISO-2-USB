// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Writing.Raw;

public enum HybridStyle
{
    /// <summary>Debian, Kali: an MBR with the ISO (type 0x17) and an appended ESP, no GPT.</summary>
    DebianMbr,

    /// <summary>Ubuntu: the same plus a GPT; the MBR has no protective entry, so Linux reads the MBR.</summary>
    UbuntuMbrAndGpt,

    /// <summary>xorriso's --protective-msdos-label with a GPT.</summary>
    ProtectiveLabelAndGpt,

    /// <summary>Only the isohybrid MBR, one entry.</summary>
    Plain,
}

public sealed record DiskPartition(string Type, long Start, long Size, string? Name, bool Bootable);

/// <summary>Hybrid ISOs made by xorriso and views of disk images taken with sfdisk, the reference implementations of both.</summary>
internal static class HybridImages
{
    public static string BuildIso(TestDirectory dir, HybridStyle style)
    {
        var tree = dir.File("iso-tree-" + style);
        Directory.CreateDirectory(Path.Combine(tree, "boot"));
        Directory.CreateDirectory(Path.Combine(tree, "live"));
        File.WriteAllBytes(Path.Combine(tree, "live", "filesystem.squashfs"), TestDirectory.Compressible(400_000));
        File.WriteAllBytes(Path.Combine(tree, "boot", "isolinux.bin"), IsoBuilder.FakeBootLoader());

        var code = new byte[432];
        code[0] = 0xFA;
        code[1] = 0xEB;
        code[2] = 0xFE;
        var mbr = dir.Write("isohdpfx.bin", code);
        var esp = dir.Write("efi.img", new byte[2 * 1024 * 1024]);

        var iso = dir.File($"{style}.iso");
        var arguments = new List<string>
        {
            "-as", "mkisofs", "-quiet", "-V", "LIVE", "-o", iso, "-r", "-J", "-iso-level", "3",
            "-b", "boot/isolinux.bin", "-c", "boot/boot.cat", "-no-emul-boot", "-boot-load-size", "4", "-boot-info-table",
            "-isohybrid-mbr", mbr,
        };
        switch (style)
        {
            case HybridStyle.DebianMbr:
                arguments.AddRange(["-partition_offset", "16", "-append_partition", "2", "0xef", esp]);
                break;
            case HybridStyle.UbuntuMbrAndGpt:
                arguments.AddRange(["-isohybrid-gpt-basdat", "-append_partition", "2", "0xef", esp]);
                break;
            case HybridStyle.ProtectiveLabelAndGpt:
                arguments.AddRange(["-partition_offset", "16", "-isohybrid-gpt-basdat", "--protective-msdos-label", "-append_partition", "2", "0xef", esp]);
                break;
        }

        arguments.Add(tree);
        ReferenceTool.Run("xorriso", arguments, dir.Path, null, null);
        return iso;
    }

    /// <summary>The table sfdisk sees: label ("dos" or "gpt") and partitions in table order.</summary>
    public static (string Label, IReadOnlyList<DiskPartition> Partitions) Table(string imagePath)
    {
        var json = ReferenceTool.Run("sfdisk", ["--json", imagePath], null, null, null).StandardOutput;
        using var document = JsonDocument.Parse(json);
        var table = document.RootElement.GetProperty("partitiontable");
        var partitions = new List<DiskPartition>();
        if (table.TryGetProperty("partitions", out var list))
        {
            foreach (var entry in list.EnumerateArray())
            {
                partitions.Add(new DiskPartition(
                    entry.GetProperty("type").GetString()!,
                    entry.GetProperty("start").GetInt64(),
                    entry.GetProperty("size").GetInt64(),
                    entry.TryGetProperty("name", out var name) ? name.GetString() : null,
                    entry.TryGetProperty("bootable", out var bootable) && bootable.GetBoolean()));
            }
        }

        return (table.GetProperty("label").GetString()!, partitions);
    }

    /// <summary>Copies bytes <paramref name="offset"/> to <paramref name="offset"/> + <paramref name="length"/> of the disk into a file of their own, for tools that want a file system image.</summary>
    public static string Extract(TestDirectory dir, string diskPath, long offset, long length, string name)
    {
        var target = dir.File(name);
        using var source = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destination = File.Create(target);
        source.Position = offset;
        var buffer = new byte[1 << 20];
        for (var left = length; left > 0;)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            Assert.True(read > 0);
            destination.Write(buffer, 0, read);
            left -= read;
        }

        return target;
    }
}
