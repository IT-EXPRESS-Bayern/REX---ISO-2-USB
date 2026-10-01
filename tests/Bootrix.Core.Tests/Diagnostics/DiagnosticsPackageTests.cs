// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Bootrix.Core.Diagnostics;

namespace Bootrix.Core.Tests.Diagnostics;

public sealed class DiagnosticsPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-diag-" + Guid.NewGuid().ToString("N")[..10]);

    public DiagnosticsPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Log(string name, string text, DateTime written)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private static List<string> Names(MemoryStream package)
    {
        package.Position = 0;
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        return [.. zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void ThePackageHoldsTheSystemDescriptionAndTheNewestLogs()
    {
        var now = DateTime.UtcNow;
        Log("bootrix-1.log", "oldest", now.AddDays(-3));
        Log("bootrix-2.log", "middle", now.AddDays(-2));
        Log("bootrix-3.log", "newest", now.AddDays(-1));
        Log("notes.txt", "not a log", now);
        using var package = new MemoryStream();

        DiagnosticsPackage.Write(package, [new DiagnosticsSource(_root, "*.log", "app-logs", MaxFiles: 2)], "Bootrix test");

        Assert.Equal(["app-logs/bootrix-2.log", "app-logs/bootrix-3.log", "system-info.txt"], Names(package));
    }

    [Fact]
    public void AMissingFolderIsSkipped()
    {
        using var package = new MemoryStream();

        DiagnosticsPackage.Write(package, [new DiagnosticsSource(Path.Combine(_root, "none"), "*.log", "x")], "info");

        Assert.Equal(["system-info.txt"], Names(package));
    }

    [Fact]
    public void ALogThatIsStillOpenForWritingIsIncluded()
    {
        var path = Path.Combine(_root, "bootrix-live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("running"u8);
        writer.Flush();
        using var package = new MemoryStream();

        DiagnosticsPackage.Write(package, [new DiagnosticsSource(_root, "*.log", "app-logs")], "info");

        package.Position = 0;
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(zip.GetEntry("app-logs/bootrix-live.log")!.Open());
        Assert.Equal("running", reader.ReadToEnd());
    }

    [Fact]
    public void ArchivesFromElsewhereAreCopiedInBelowTheirPrefix()
    {
        using var broker = new MemoryStream();
        using (var zip = new ZipArchive(broker, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("broker-logs/broker-1.log").Open());
            entry.Write("broker text");
        }

        broker.Position = 0;
        using var package = new MemoryStream();

        DiagnosticsPackage.Write(package, [], "info", [("broker", broker)]);

        Assert.Equal(["broker/broker-logs/broker-1.log", "system-info.txt"], Names(package));
    }

    [Fact]
    public void ThePathsInAForeignArchiveCannotLeaveTheirPrefix()
    {
        using var other = new MemoryStream();
        using (var zip = new ZipArchive(other, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("../../evil.txt").Open());
            entry.Write("x");
        }

        other.Position = 0;
        using var package = new MemoryStream();

        DiagnosticsPackage.Write(package, [], "info", [("broker", other)]);

        Assert.DoesNotContain(Names(package), n => n.Contains("..", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSystemDescriptionNamesNeitherMachineNorUser()
    {
        var text = SystemReport.Describe(elevated: true);

        Assert.Contains("Bootrix", text, StringComparison.Ordinal);
        Assert.Contains("Elevated: True", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
    }
}
