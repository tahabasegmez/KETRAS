using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Ketras.Utilities;

/// <summary>
/// Logger provider: one text file per day (<c>ketras-yyyyMMdd.log</c>) in a folder, the oldest removed after
/// <see cref="KeepDays"/> days. Lines are queued and written on a background task, so logging never blocks a caller;
/// a write failure drops the line.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>How many daily files are kept.</summary>
    public const int KeepDays = 14;

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;

    /// <summary>Writes into <paramref name="directory"/>, created when missing.</summary>
    public FileLoggerProvider(string directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _writer = Task.Run(WriteAsync);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Writes what is queued and stops.</summary>
    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(string line) => _lines.Writer.TryWrite(line);

    private async Task WriteAsync()
    {
        StreamWriter? file = null;
        var day = DateOnly.MinValue;
        try
        {
            Directory.CreateDirectory(_directory);
            RemoveOld();
            await foreach (var line in _lines.Reader.ReadAllAsync())
            {
                var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
                if (file is null || today != day)
                {
                    if (file is not null)
                    {
                        await file.DisposeAsync();
                    }

                    day = today;
                    file = new StreamWriter(new FileStream(Path.Combine(_directory, $"ketras-{today:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }

                await file.WriteLineAsync(line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a writable log folder the application runs on; lines are dropped.
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync();
            }
        }
    }

    private void RemoveOld()
    {
        var files = Directory.GetFiles(_directory, "ketras-*.log").Order(StringComparer.Ordinal).ToList();
        foreach (var old in files.Take(Math.Max(0, files.Count - KeepDays)))
        {
            File.Delete(old);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var time = provider._time.GetLocalNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} {Level(logLevel)} {category}: {formatter(state, exception)}";
            provider.Enqueue(exception is null ? line : $"{line}{System.Environment.NewLine}{exception}");
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit"
        };
    }
}
