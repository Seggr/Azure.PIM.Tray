using System.Collections.Concurrent;
using System.IO;

namespace Azure.PIM.Tray.Services;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// In-memory structured log with optional disk persistence.
/// Entries are cleared on restart. Thread-safe.
/// Disk logging uses up to 3 rolling files of 10 MB each (pim.log, pim.1.log, pim.2.log).
/// </summary>
public static class AppLog
{
    private static readonly ConcurrentQueue<LogEntry> _entries = new();
    private static readonly object _fileLock = new();

    private const long MaxFileSizeBytes = 10L * 1024 * 1024; // 10 MB
    private const int  MaxRollingFiles  = 3;

    /// <summary>Whether log entries are written to disk. Off by default; toggled via the Log Viewer.</summary>
    public static bool LogToDisk { get; set; } = false;

    public static string LogDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Azure.PIM.Tray", "logs");

    public static LogLevel MinLevel { get; set; } = LogLevel.Warning;

    /// <summary>Raised on the writing thread after every accepted entry.</summary>
    public static event Action? EntryAdded;

    public static void Debug  (string source, string message) => Add(LogLevel.Debug,   source, message);
    public static void Info   (string source, string message) => Add(LogLevel.Info,    source, message);
    public static void Warning(string source, string message) => Add(LogLevel.Warning, source, message);
    public static void Error  (string source, string message) => Add(LogLevel.Error,   source, message);

    private const int MaxEntries = 2000;

    public static void Add(LogLevel level, string source, string message)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message);
        _entries.Enqueue(entry);

        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);

        WriteToFile(entry);
        System.Diagnostics.Debug.WriteLine($"[{level,-7}] [{source}] {message}");
        EntryAdded?.Invoke();
    }

    public static IReadOnlyList<LogEntry> GetAll() => _entries.ToArray();
    public static int ErrorCount   => _entries.Count(e => e.Level == LogLevel.Error);
    public static int WarningCount => _entries.Count(e => e.Level == LogLevel.Warning);
    public static int Count        => _entries.Count;

    public static void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        EntryAdded?.Invoke();
    }

    private static string CurrentLogFile => Path.Combine(LogDir, "pim.log");
    public static string LogFilePath => CurrentLogFile;

    private static void RotateIfNeeded()
    {
        var current = CurrentLogFile;
        if (!File.Exists(current)) return;
        if (new FileInfo(current).Length < MaxFileSizeBytes) return;

        // Delete the oldest backup to make room
        var oldest = Path.Combine(LogDir, $"pim.{MaxRollingFiles - 1}.log");
        if (File.Exists(oldest)) File.Delete(oldest);

        // Shift backups up by one
        for (int i = MaxRollingFiles - 2; i >= 1; i--)
        {
            var src  = Path.Combine(LogDir, $"pim.{i}.log");
            var dest = Path.Combine(LogDir, $"pim.{i + 1}.log");
            if (File.Exists(src)) File.Move(src, dest, overwrite: true);
        }

        // Move the current log to .1
        File.Move(current, Path.Combine(LogDir, "pim.1.log"), overwrite: true);
    }

    private static void WriteToFile(LogEntry e)
    {
        if (!LogToDisk) return;
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(LogDir);
                RotateIfNeeded();
                File.AppendAllText(
                    CurrentLogFile,
                    $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}  [{e.Level,-7}]  [{e.Source}]  {e.Message}{Environment.NewLine}");
            }
        }
        catch { /* never let file I/O crash the app */ }
    }
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Source, string Message);
