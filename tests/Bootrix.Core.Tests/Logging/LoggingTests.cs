// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Tests.Logging;

public sealed class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("key AAAAA-BBBBB-CCCCC-DDDDD-EEEEE set", "key *****-*****-*****-*****-***** set")]
    [InlineData("password=hunter2 next", "password=*** next")]
    [InlineData("Passwort: geheim;", "Passwort=***;")]
    [InlineData("nothing to hide", "nothing to hide")]
    public void RedactsSecrets(string input, string expected)
    {
        Assert.Equal(expected, LogRedactor.Redact(input));
    }

    [Fact]
    public void WritesRedactedLinesToDailyFile()
    {
        using (var provider = new FileLoggerProvider(_dir))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("started with password=abc123");
            logger.LogDebug("hidden by level");
            logger.LogError(new InvalidOperationException("bad"), "failed");
        }

        var file = Assert.Single(Directory.GetFiles(_dir, "bootrix-*.log"));
        var text = File.ReadAllText(file);

        Assert.Contains("[INF] Test: started with password=***", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("hidden by level", text);
        Assert.Contains("[ERR] Test: failed", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void FilePrefix_SeparatesTheFilesOfDifferentProcesses()
    {
        using (var broker = new FileLoggerProvider(_dir, filePrefix: "broker"))
        using (var app = new FileLoggerProvider(_dir))
        {
            broker.CreateLogger("Broker").LogInformation("from the elevated process");
            app.CreateLogger("App").LogInformation("from the window");
        }

        var brokerLog = Assert.Single(Directory.GetFiles(_dir, "broker-*.log"));
        var appLog = Assert.Single(Directory.GetFiles(_dir, "bootrix-*.log"));
        Assert.Contains("from the elevated process", File.ReadAllText(brokerLog));
        Assert.DoesNotContain("from the window", File.ReadAllText(brokerLog));
        Assert.Contains("from the window", File.ReadAllText(appLog));
    }

    [Fact]
    public void OldFilesAreOnlyDeletedForTheOwnPrefix()
    {
        Directory.CreateDirectory(_dir);
        var oldBroker = Path.Combine(_dir, "broker-20200101.log");
        var oldApp = Path.Combine(_dir, "bootrix-20200101.log");
        File.WriteAllText(oldBroker, "x");
        File.WriteAllText(oldApp, "x");
        File.SetLastWriteTimeUtc(oldBroker, DateTime.UtcNow.AddDays(-90));
        File.SetLastWriteTimeUtc(oldApp, DateTime.UtcNow.AddDays(-90));

        using (new FileLoggerProvider(_dir, filePrefix: "broker"))
        {
        }

        Assert.False(File.Exists(oldBroker));
        Assert.True(File.Exists(oldApp));
    }
}
