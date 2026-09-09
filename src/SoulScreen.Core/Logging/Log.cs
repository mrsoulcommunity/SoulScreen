using System.Diagnostics;

namespace SoulScreen.Core.Logging;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

public readonly record struct LogEntry(DateTime TimestampUtc, LogLevel Level, string Category, string Message, Exception? Exception)
{
    public override string ToString()
    {
        var line = $"{TimestampUtc.ToLocalTime():HH:mm:ss.fff} {Level.ToString().ToUpperInvariant(),-5} [{Category}] {Message}";
        return Exception is null ? line : $"{line}\n{Exception}";
    }
}

/// <summary>
/// Deliberately tiny process-wide log sink. The protocol code runs on many threads and
/// the WPF shell wants a live tail of it, so everything funnels through one event.
/// </summary>
public static class Log
{
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>Raised for every entry at or above <see cref="MinimumLevel"/>. Handlers must be thread-safe.</summary>
    public static event Action<LogEntry>? Entry;

    public static ILogger For(string category) => new Logger(category);

    internal static void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < MinimumLevel) return;
        var entry = new LogEntry(DateTime.UtcNow, level, category, message, exception);
        Debug.WriteLine(entry.ToString());
        Entry?.Invoke(entry);
    }
}

public interface ILogger
{
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
}

internal sealed class Logger(string category) : ILogger
{
    public void Trace(string message) => Log.Write(LogLevel.Trace, category, message, null);
    public void Debug(string message) => Log.Write(LogLevel.Debug, category, message, null);
    public void Info(string message) => Log.Write(LogLevel.Info, category, message, null);
    public void Warn(string message, Exception? exception = null) => Log.Write(LogLevel.Warn, category, message, exception);
    public void Error(string message, Exception? exception = null) => Log.Write(LogLevel.Error, category, message, exception);
}
