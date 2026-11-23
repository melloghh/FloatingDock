using System.Net;

namespace FloatingDock.Ipc;

/// <summary>
/// Where the engine listens. The transport is TCP on the loopback interface (a random port) guarded by a random token: Unix-domain sockets
/// turned out to be unreliable on Windows (connect fails with error 10022, files of dead sockets cannot be deleted, error 1920).
/// An endpoint is the string <c>tcp:&lt;port&gt;:&lt;token&gt;</c>; the running engine publishes it in <c>engine.endpoint</c> in the profile folder,
/// which only the profile's user can read. Mirrored by <c>defaultEndpoint()</c> in Dart.
/// </summary>
public static class IpcEndpoint
{
    private const string PointerName = "engine.endpoint";

    public static string PointerPath(string profileRoot) => Path.Combine(profileRoot, PointerName);

    /// <summary>The endpoint of the engine that is running for this profile, or null.</summary>
    public static string? Read(string profileRoot)
    {
        try
        {
            var path = PointerPath(profileRoot);
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null;
        }
        catch (IOException) { return null; }
    }

    public static void Publish(string profileRoot, string endpoint)
    {
        try
        {
            Directory.CreateDirectory(profileRoot);
            File.WriteAllText(PointerPath(profileRoot), endpoint);
        }
        catch (IOException) { }
    }

    public static void ClearPublished(string profileRoot)
    {
        try { File.Delete(PointerPath(profileRoot)); } catch (IOException) { }
    }

    public static string Format(int port, string token) => $"tcp:{port}:{token}";

    public static (IPEndPoint Address, string Token) Parse(string endpoint)
    {
        var parts = endpoint.Split(':');
        if (parts.Length != 3 || parts[0] != "tcp" || !int.TryParse(parts[1], out int port)) throw new FormatException("Not an engine endpoint: " + endpoint);
        return (new IPEndPoint(IPAddress.Loopback, port), parts[2]);
    }
}
