// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Bootrix.Core.Tests.Images.Support;

public sealed record MbrPartitionSpec(long StartSector, long SectorCount, string Type, bool Bootable = false);

public sealed record GptPartitionSpec(long StartSector, long SectorCount, string TypeCode, string Name);

/// <summary>Creates sparse disk images and partitions them with sfdisk and sgdisk, the reference tools for the partition table readers.</summary>
public static class DiskImageBuilder
{
    public static string Create(TestDirectory dir, string name, long sizeBytes)
    {
        var path = dir.File(name);
        using var file = File.Create(path);
        file.SetLength(sizeBytes);
        return path;
    }

    public static string CreateMbr(TestDirectory dir, string name, long sizeBytes, params MbrPartitionSpec[] partitions)
    {
        var path = Create(dir, name, sizeBytes);
        var script = new StringBuilder("label: dos\n");
        foreach (var partition in partitions)
        {
            script.Append(CultureInfo.InvariantCulture, $"start={partition.StartSector}, size={partition.SectorCount}, type={partition.Type}");
            script.Append(partition.Bootable ? ", bootable\n" : "\n");
        }

        var scriptFile = dir.File(name + ".sfdisk");
        File.WriteAllText(scriptFile, script.ToString());
        ExternalTool.Run("sfdisk", ["--quiet", path], null, scriptFile, null);
        return path;
    }

    public static string CreateGpt(TestDirectory dir, string name, long sizeBytes, params GptPartitionSpec[] partitions)
    {
        var path = Create(dir, name, sizeBytes);
        // sgdisk pauses for a second after every run, so all partitions are created in a single call.
        var arguments = new List<string>();
        var number = 1;
        foreach (var partition in partitions)
        {
            arguments.AddRange(
            [
                "-n", $"{number}:{partition.StartSector}:{partition.StartSector + partition.SectorCount - 1}",
                "-t", $"{number}:{partition.TypeCode}",
                "-c", $"{number}:{partition.Name}",
            ]);
            number++;
        }

        arguments.Add(path);
        ExternalTool.Run("sgdisk", arguments, null, null, null);
        return path;
    }

    /// <summary>Copies <paramref name="content"/> to <paramref name="offset"/> of the image (the way a partition's file system gets into it).</summary>
    public static void WriteAt(string path, long offset, byte[] content)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.Position = offset;
        file.Write(content);
    }
}
