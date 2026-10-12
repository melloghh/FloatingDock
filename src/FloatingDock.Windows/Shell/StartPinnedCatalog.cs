using System.Diagnostics;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;

namespace FloatingDock.Windows.Shell;

/// <summary>Uses the supported, read-only Start export. Never parses Start's private databases or changes its layout.</summary>
public static class StartPinnedCatalog
{
    public sealed record Result(List<AppRef> Apps, int ExportedPins, IReadOnlyList<string> Warnings);

    public static async Task<Result> LoadAsync(ShellWorker worker, CancellationToken cancellation = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "FloatingDock-Start-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var command = "Export-StartLayout -Path '" + temporary.Replace("'", "''") + "' -ErrorAction Stop";
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Could not export the Windows Start pins.");
            var error = process.StandardError.ReadToEndAsync(cancellation);
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(20), cancellation); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            string problem = await error;
            await output;
            if (process.ExitCode != 0 || !File.Exists(temporary)) throw new IOException("Windows could not export Start pins. " + problem.Trim());
            var pins = StartLayoutPins.Parse(await File.ReadAllTextAsync(temporary, cancellation));
            return await worker.Run(() => Resolve(pins));
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static Result Resolve(IReadOnlyList<StartLayoutPin> pins)
    {
        var apps = new List<AppRef>();
        var warnings = new List<string>();
        List<AppRef>? catalog = null;
        var seen = new HashSet<string>();
        foreach (var pin in pins)
        {
            AppRef? app;
            if (pin.Kind == "desktopAppLink")
            {
                string path = Environment.ExpandEnvironmentVariables(pin.Value);
                app = File.Exists(path) ? ShellHelpers.AppFromShortcut(path, AppSource.StartMenu) : null;
            }
            else
            {
                catalog ??= AppCatalog.Load();
                app = catalog.FirstOrDefault(a => string.Equals(a.Aumid, pin.Value, StringComparison.OrdinalIgnoreCase) || string.Equals(a.ShellParsingName, pin.Value, StringComparison.OrdinalIgnoreCase))?.Clone();
            }
            if (app is null) { warnings.Add("An unavailable Start pin was skipped."); continue; }
            if (seen.Add(app.Key)) apps.Add(app);
        }
        return new(apps, pins.Count, warnings);
    }
}
