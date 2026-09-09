using System.Text;

namespace SoulScreen.Core.Logging;

/// <summary>
/// Mirrors the log to a rolling file.
/// <para>
/// When a phone will not connect, the interesting evidence is a handshake that happened
/// seconds ago and scrolled away, or a crash that closed the window before anyone could
/// read it. A file on disk survives both.
/// </para>
/// </summary>
public sealed class FileLogSink : IDisposable
{
    private readonly object _writeLock = new();
    private readonly string _path;
    private readonly long _maxBytes;

    private StreamWriter? _writer;
    private long _written;

    private FileLogSink(string path, long maxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
        Open();
    }

    /// <summary>Path of the file currently being written.</summary>
    public string Path => _path;

    /// <summary>Folder holding the current log and the one rolled before it.</summary>
    public string LogDirectory => System.IO.Path.GetDirectoryName(_path) ?? string.Empty;

    /// <summary>
    /// Starts writing every log entry to <c>&lt;directory&gt;/logs/soulscreen.log</c>.
    /// Returns null if the file cannot be opened, since losing the log is never a reason
    /// to stop the app.
    /// </summary>
    public static FileLogSink? Attach(string directory, long maxBytes = 4 * 1024 * 1024)
    {
        try
        {
            var logDirectory = System.IO.Path.Combine(directory, "logs");
            Directory.CreateDirectory(logDirectory);
            var sink = new FileLogSink(System.IO.Path.Combine(logDirectory, "soulscreen.log"), maxBytes);
            Log.Entry += sink.Write;
            return sink;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Open()
    {
        var info = new FileInfo(_path);
        // Roll once rather than accumulate: one previous run is enough history to compare
        // a working session against a broken one.
        if (info.Exists && info.Length > _maxBytes)
        {
            var previous = _path + ".1";
            try
            {
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(_path, previous);
            }
            catch (IOException) { /* keep appending rather than lose the sink */ }
        }

        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _written = new FileInfo(_path).Length;

        _writer.WriteLine();
        _writer.WriteLine($"=== SoulScreen started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    private void Write(LogEntry entry)
    {
        lock (_writeLock)
        {
            var writer = _writer;
            if (writer is null) return;

            try
            {
                var line = entry.ToString();
                writer.WriteLine(line);
                _written += line.Length + 2;

                if (_written > _maxBytes)
                {
                    writer.Dispose();
                    _writer = null;
                    Open();
                }
            }
            catch (Exception)
            {
                // A failing sink must not take the application with it.
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        Log.Entry -= Write;
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
