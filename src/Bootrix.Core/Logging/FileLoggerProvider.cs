// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information, int retainDays = 30, TimeProvider? time = null)
    {
        _directory = directory;
        _minimum = minimum;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        DeleteOldFiles(retainDays);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void DeleteOldFiles(int retainDays)
    {
        var limit = _time.GetUtcNow().UtcDateTime.AddDays(-retainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "bootrix-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Still open in another instance; it will be removed on a later start.
            }
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelTag(level)).Append("] ")
            .Append(category).Append(": ")
            .Append(LogRedactor.Redact(message));
        if (exception is not null)
        {
            line.AppendLine().Append(LogRedactor.Redact(exception.ToString()));
        }

        lock (_gate)
        {
            var day = DateOnly.FromDateTime(now.DateTime);
            if (_writer is null || day != _currentDay)
            {
                _writer?.Dispose();
                var path = Path.Combine(_directory, $"bootrix-{day:yyyyMMdd}.log");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _currentDay = day;
            }

            _writer.WriteLine(line.ToString());
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
