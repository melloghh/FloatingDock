namespace FloatingDock.Engine;

/// <summary>Append-only log in the profile's Logs folder; rolled over at 1 MB. Never throws.</summary>
internal static class EngineLog
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Open(string logsDir)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            var file = Path.Combine(logsDir, "engine.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".1", overwrite: true);
            _file = file;
        }
        catch (Exception) { _file = null; }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + ": " + ex);

    private static void Write(string level, string message)
    {
        var file = _file;
        if (file is null) return;
        try { lock (Gate) File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}"); }
        catch (Exception) { }
    }
}
