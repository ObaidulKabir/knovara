using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Knovara.Local.Diagnostics;

/// <summary>
/// A minimal file logger for the local runtime, used only when it is launched hidden (no console) by the CLI or the MCP
/// server, so there is still somewhere to look when ingestion or a query misbehaves. One line per log entry, appended with
/// a simple size-based roll, with no third-party logging package.
/// </summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private const long MaxBytesBeforeRoll = 5 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Lock _writeLock = new();

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Write(string line)
    {
        lock (_writeLock)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytesBeforeRoll)
                {
                    File.Copy(path, path + ".1", overwrite: true);
                    File.Delete(path);
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never crash the app it is observing.
            }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {message}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
