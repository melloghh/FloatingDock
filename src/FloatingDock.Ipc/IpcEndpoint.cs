namespace FloatingDock.Ipc;

public static class IpcEndpoint
{
    /// <summary>The socket lives in the profile folder, so only that user can reach it. Mirrored by <c>defaultSocketPath()</c> in Dart.</summary>
    public static string SocketPathFor(string profileRoot) => Path.Combine(profileRoot, "engine.sock");
}
