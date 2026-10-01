// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Writing.Raw;

namespace Bootrix.Core.Tests.Writing.Raw;

public sealed class GptRelocationTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TestDirectory _dir = new("bootrix-gpt");

    public void Dispose() => _dir.Dispose();

    private string Stick(byte[] image, long size = 64 * MiB)
    {
        var path = _dir.File($"stick-{Guid.NewGuid():N}.img");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.SetLength(size);
        file.Write(image);
        return path;
    }

    private static GptRelocationResult Move(string path, int sectorSize = 512)
    {
        using var device = new FileBlockDevice(path, new FileInfo(path).Length, sectorSize, create: false);
        return GptRelocation.MoveToDiskEnd(device);
    }

    private string GptImage() => DiskImageBuilder.CreateGpt(
        _dir,
        "disk.img",
        16 * MiB,
        new GptPartitionSpec(2048, 8192, "AF00", "Apple HFS"),
        new GptPartitionSpec(10240, 8192, "AF0A", "Apple APFS"));

    [ToolFact("sgdisk", "sfdisk")]
    public void ImageOnALargerDisk_GetsItsBackupAtTheEnd_AndSgdiskIsHappy()
    {
        var stick = Stick(File.ReadAllBytes(GptImage()));
        var before = ReferenceTool.Run("sgdisk", ["-p", stick], null, null, null).StandardOutput;
        var detailsBefore = ReferenceTool.Run("sgdisk", ["-i", "1", stick], null, null, null).StandardOutput;
        Assert.Contains("Problem", ReferenceTool.Run("sgdisk", ["--verify", stick], null, null, null).StandardOutput, StringComparison.Ordinal);

        Assert.Equal(GptRelocationResult.Moved, Move(stick));

        var verify = ReferenceTool.Run("sgdisk", ["--verify", stick], null, null, null).StandardOutput;
        Assert.Contains("No problems found", verify, StringComparison.Ordinal);
        Assert.Equal(detailsBefore, ReferenceTool.Run("sgdisk", ["-i", "1", stick], null, null, null).StandardOutput);
        var after = ReferenceTool.Run("sgdisk", ["-p", stick], null, null, null).StandardOutput;
        Assert.Contains("Total free space is", after, StringComparison.Ordinal);
        Assert.Equal(PartitionLines(before), PartitionLines(after));
        Assert.Contains($"last usable sector is {(64 * MiB / 512) - 34}", after, StringComparison.Ordinal);
    }

    [ToolFact("sgdisk")]
    public void SecondRun_FindsNothingToDo()
    {
        var stick = Stick(File.ReadAllBytes(GptImage()));
        Move(stick);
        var snapshot = File.ReadAllBytes(stick);

        Assert.Equal(GptRelocationResult.AlreadyAtEnd, Move(stick));
        Assert.Equal(snapshot, File.ReadAllBytes(stick));
    }

    [ToolFact("sgdisk")]
    public void ImageThatFillsTheDisk_IsAlreadyInPlace()
    {
        var path = GptImage();

        Assert.Equal(GptRelocationResult.AlreadyAtEnd, Move(path));
    }

    [ToolFact("sgdisk")]
    public void HybridMbr_IsLeftAlone()
    {
        var path = GptImage();
        ReferenceTool.Run("sgdisk", ["--hybrid", "1", path], null, null, null);
        var stick = Stick(File.ReadAllBytes(path));
        var before = File.ReadAllBytes(stick);

        Assert.Equal(GptRelocationResult.NotApplicable, Move(stick));
        Assert.Equal(before, File.ReadAllBytes(stick));
    }

    [Fact]
    public void DiskWithoutAGpt_IsNotApplicable()
    {
        var stick = Stick(new byte[4096]);

        Assert.Equal(GptRelocationResult.NotApplicable, Move(stick));
    }

    [ToolFact("sgdisk")]
    public void FourKnSectors_AreNotApplicable()
    {
        var stick = Stick(File.ReadAllBytes(GptImage()));

        Assert.Equal(GptRelocationResult.NotApplicable, Move(stick, sectorSize: 4096));
    }

    private static string[] PartitionLines(string sgdiskOutput) =>
        [.. sgdiskOutput.Split('\n').Where(line => line.TrimStart().Length > 0 && char.IsDigit(line.TrimStart()[0]) && line.Contains("MiB", StringComparison.Ordinal))];
}
