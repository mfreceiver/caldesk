using System.IO;

namespace CalDesk.App.Diagnostics;

/// <summary>Minimal crash-safe file logger used to diagnose startup hangs / silent exits.
/// Each line is appended and flushed individually, so whatever was logged last survives a
/// hard freeze or crash — the last timestamped line points at the phase that hung.
/// Output: <c>%AppData%\CalDesk\caldesk.log</c> (same folder as the DB).
/// No-op until <see cref="Init"/> has been called, so it is safe to call from code paths
/// exercised by tests that never initialise it.</summary>
internal static class StartupLog
{
    private static readonly object _gate = new();
    private static string? _path;

    /// <summary>Resolved log path (null until <see cref="Init"/>).</summary>
    public static string? Path => _path;

    /// <summary>Open the log: ensure the folder exists, trim an over-grown file, and write a
    /// run separator with environment context. Returns the log path.</summary>
    public static string Init()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CalDesk");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "caldesk.log");

        try
        {
            var fi = new FileInfo(_path);
            // Bound growth: if the log got huge (many runs), start fresh this run.
            if (fi.Exists && fi.Length > 1_000_000)
            {
                File.WriteAllText(_path, "");
            }

            File.AppendAllText(_path,
                $"\n==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  CalDesk start" +
                $"  pid={Environment.ProcessId}  {(Environment.Is64BitProcess ? "x64" : "x86")}" +
                $"  clr={Environment.Version}  os={Environment.OSVersion.Version} ====\n");
        }
        catch
        {
            // Logging must never break startup.
        }

        return _path;
    }

    /// <summary>Append a timestamped line. Flushed on every call. Failures are swallowed.</summary>
    public static void Log(string message)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}\n";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line);
            }
            catch
            {
                // ignore — diagnostics must not crash the app
            }
        }
    }

    /// <summary>Append an exception with type, message, and stack trace.</summary>
    public static void Log(Exception ex, string context)
    {
        Log($"{context}: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
        if (ex.InnerException is { } inner)
        {
            Log($"  inner: {inner.GetType().FullName}: {inner.Message}");
        }
    }
}
