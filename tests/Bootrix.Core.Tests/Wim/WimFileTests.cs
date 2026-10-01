// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Wim;

namespace Bootrix.Core.Tests.Wim;

/// <summary>Runs only where wimlib and its command line tools are installed (they are on the Linux CI image).</summary>
public sealed class NeedsWimlibFactAttribute : FactAttribute
{
    public NeedsWimlibFactAttribute()
    {
        if (!WimFile.IsAvailable)
        {
            Skip = "wimlib is not installed";
        }
        else if (!WimTestTools.HasTool("wimcapture") || !WimTestTools.HasTool("wimapply"))
        {
            Skip = "wimtools are not installed";
        }
    }
}

internal static class WimTestTools
{
    public static bool HasTool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, name)));
    }

    public static (int ExitCode, string Output) Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}

public sealed class WimFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-wim-" + Guid.NewGuid().ToString("N"));

    public WimFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateSampleTree(int files, int sizeBytes)
    {
        var tree = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        var random = new Random(11);
        for (var i = 0; i < files; i++)
        {
            var data = new byte[sizeBytes + i * 1000];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(tree, i % 2 == 0 ? "sub" : "", $"file{i}.bin"), data);
        }

        return tree;
    }

    private string Capture(string tree, string name = "sample.wim", string compression = "none")
    {
        var wim = Path.Combine(_dir, name);
        var (code, output) = WimTestTools.Run("wimcapture", tree, wim, "Edition One", "--compress=" + compression);
        Assert.True(code == 0, output);
        return wim;
    }

    [NeedsWimlibFact]
    public void OpenReadsInfoAndImageProperties()
    {
        var wim = Capture(CreateSampleTree(4, 100_000), compression: "LZX");

        using var file = WimFile.Open(wim);
        var info = file.Info;

        Assert.Equal(1, info.ImageCount);
        Assert.Equal(WimCompression.Lzx, info.Compression);
        Assert.Equal(1, info.PartNumber);
        Assert.Equal(1, info.TotalParts);
        Assert.Equal("Edition One", file.GetImageProperty(1, "NAME"));
        Assert.Null(file.GetImageProperty(1, "NO/SUCH/PROPERTY"));
    }

    [NeedsWimlibFact]
    public void SplitProducesPartsWithinTheLimitThatApplyBackIdentically()
    {
        var tree = CreateSampleTree(12, 700_000);
        var wim = Capture(tree);
        var first = Path.Combine(_dir, "install.swm");

        using (var file = WimFile.Open(wim))
        {
            var progress = new List<double>();
            file.Split(first, 3 * 1024 * 1024, new SyncProgress(progress.Add));
            Assert.NotEmpty(progress);
            Assert.True(progress[^1] > 0.9);
        }

        var parts = Directory.GetFiles(_dir, "install*.swm").OrderBy(p => p).ToList();
        Assert.True(parts.Count >= 3, $"expected several parts, got {parts.Count}");
        Assert.All(parts, p => Assert.True(new FileInfo(p).Length <= 3 * 1024 * 1024 + 64 * 1024, p));

        using (var firstPart = WimFile.Open(first))
        {
            Assert.Equal(1, firstPart.Info.PartNumber);
            Assert.Equal(parts.Count, firstPart.Info.TotalParts);
        }

        var target = Path.Combine(_dir, "restored");
        var (code, output) = WimTestTools.Run("wimapply", first, "1", target, "--ref=" + Path.Combine(_dir, "install*.swm"));
        Assert.True(code == 0, output);

        var (diffCode, diffOutput) = WimTestTools.Run("diff", "-r", tree, target);
        Assert.True(diffCode == 0, diffOutput);
    }

    [NeedsWimlibFact]
    public void CancellationAbortsTheSplit()
    {
        var wim = Capture(CreateSampleTree(30, 400_000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var file = WimFile.Open(wim);

        Assert.ThrowsAny<OperationCanceledException>(() => file.Split(Path.Combine(_dir, "c.swm"), 2 * 1024 * 1024, null, cts.Token));
    }

    [NeedsWimlibFact]
    public void OpeningAMissingFileThrowsBootrixException()
    {
        var ex = Assert.Throws<BootrixException>(() => WimFile.Open(Path.Combine(_dir, "missing.wim")));

        Assert.Equal(ErrorCode.ExternalToolFailed, ex.Code);
    }

    [NeedsWimlibFact]
    public void WriteImageRecompressesIntoANewFile()
    {
        var wim = Capture(CreateSampleTree(6, 300_000));
        var output = Path.Combine(_dir, "single.wim");

        using (var file = WimFile.Open(wim))
        {
            file.WriteImage(output, 1, WimCompression.Lzx, recompress: true);
        }

        using var result = WimFile.Open(output);
        Assert.Equal(WimCompression.Lzx, result.Info.Compression);
        Assert.Equal("Edition One", result.GetImageProperty(1, "NAME"));
    }

    [Fact]
    public void InfoStructureIsParsedAtTheDocumentedOffsets()
    {
        var info = new byte[WimLibNativeSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(20), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), 68864);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), 32768);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(32), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(34), 5);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(36), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(40), 5_000_000_000);

        var parsed = WimFile.ParseInfo(info);

        Assert.Equal(new WimInfo(3, 2, 68864, 32768, 2, 5, WimCompression.Lzms, 5_000_000_000), parsed);
    }

    private const int WimLibNativeSize = 88;

    private sealed class SyncProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
