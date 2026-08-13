using System.Text;

namespace HalimRecovery.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Minimal thread-safe rolling file logger.
/// Never log personal file contents, passwords or secrets — only operational metadata.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logFile;
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static event Action<LogLevel, string>? OnMessage;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HalimRecovery", "logs");

    private static string LogFile
    {
        get
        {
            if (_logFile == null)
            {
                Directory.CreateDirectory(LogDirectory);
                _logFile = Path.Combine(LogDirectory, $"halim-{DateTime.Now:yyyyMMdd}.log");
            }
            return _logFile;
        }
    }

    public static void Debug(string component, string message) => Write(LogLevel.Debug, component, message);
    public static void Info(string component, string message) => Write(LogLevel.Info, component, message);
    public static void Warn(string component, string message) => Write(LogLevel.Warn, component, message);
    public static void Error(string component, string message, Exception? ex = null)
        => Write(LogLevel.Error, component, ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string component, string message)
    {
        if (level < MinLevel) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] [{component}] {message}";
        try
        {
            lock (Gate) File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* logging must never crash the app */ }
        OnMessage?.Invoke(level, line);
    }
}
