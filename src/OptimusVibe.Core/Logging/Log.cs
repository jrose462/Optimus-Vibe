using System.Globalization;
using System.Text;

namespace OptimusVibe.Logging;

/// <summary>
/// Minimal console + daily file logger. Never throws: every failure inside the logger is swallowed.
/// Before <see cref="Init"/> is called, messages go to the console only.
/// </summary>
public static class Log
{
    private const int KeepFiles = 14;
    private const string FilePrefix = "optimusvibe-";
    private const string FileSuffix = ".log";

    private static readonly object Gate = new();
    private static string? _logsDir;

    /// <summary>When true, nothing is echoed to the console (file only).</summary>
    public static bool Quiet { get; set; }

    /// <summary>Enables file logging to <c>{logsDir}\optimusvibe-yyyyMMdd.log</c> and prunes old files. Safe to call twice.</summary>
    public static void Init(string logsDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logsDir)) return;
            var full = Path.GetFullPath(logsDir);
            Directory.CreateDirectory(full);
            lock (Gate)
            {
                _logsDir = full;
            }
            Prune(full);
        }
        catch
        {
            // Logging must never break the tool.
        }
    }

    public static void Info(string message) => Write("INFO", message, null, null);

    public static void Warn(string message) => Write("WARN", message, null, ConsoleColor.Yellow);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex, ConsoleColor.Red);

    private static void Write(string level, string message, Exception? ex, ConsoleColor? color)
    {
        try
        {
            message ??= "";
            lock (Gate)
            {
                if (!Quiet) WriteConsole(level, message, ex, color);
                WriteFile(level, message, ex);
            }
        }
        catch
        {
            // swallow
        }
    }

    private static void WriteConsole(string level, string message, Exception? ex, ConsoleColor? color)
    {
        try
        {
            // Warnings/errors go to stderr so they don't pollute machine-readable stdout output.
            var writer = level == "INFO" ? Console.Out : Console.Error;
            var text = level == "INFO" ? message : $"{level}: {message}";
            if (ex != null) text += $" ({ex.GetType().Name}: {ex.Message})";

            if (color.HasValue)
            {
                ConsoleColor previous;
                try { previous = Console.ForegroundColor; } catch { previous = ConsoleColor.Gray; }
                try { Console.ForegroundColor = color.Value; } catch { /* redirected / no console */ }
                try { writer.WriteLine(text); }
                finally
                {
                    try { Console.ForegroundColor = previous; } catch { /* ignore */ }
                }
            }
            else
            {
                writer.WriteLine(text);
            }
        }
        catch
        {
            // swallow
        }
    }

    private static void WriteFile(string level, string message, Exception? ex)
    {
        try
        {
            var dir = _logsDir;
            if (dir == null) return;
            var now = DateTime.Now;
            var path = Path.Combine(dir, FilePrefix + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileSuffix);
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" [").Append(level).Append("] ")
              .Append("[pid ").Append(Environment.ProcessId).Append("] ")
              .Append(message)
              .AppendLine();
            if (ex != null) sb.AppendLine(ex.ToString());

            // Several wrapper instances may log at once; open with shared access and retry briefly.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                    sw.Write(sb.ToString());
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(15);
                }
            }
        }
        catch
        {
            // swallow
        }
    }

    private static void Prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir)
                .GetFiles(FilePrefix + "*" + FileSuffix)
                .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(KeepFiles);
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* in use or no access */ }
            }
        }
        catch
        {
            // swallow
        }
    }
}
