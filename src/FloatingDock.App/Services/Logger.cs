using System.Text;

namespace FloatingDock.UI.Services;

/// <summary>Tiny thread-safe file logger (rolls over at 512 KB). Logging must never throw.</summary>
internal sealed class Logger
{
    private readonly string _file;
    private readonly object _gate = new();

    public Logger(string logsDir)
    {
        try { Directory.CreateDirectory(logsDir); } catch { /* logging is optional */ }
        _file = Path.Combine(logsDir, "floatingdock.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + " :: " + ex);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var fi = new FileInfo(_file);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    var old = _file + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_file, old);
                }
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* ignore */ }
    }
}
