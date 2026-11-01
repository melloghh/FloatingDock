using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FloatingDock.Ipc;

/// <summary>
/// Wire contract between the engine and every UI process. The prose description is <c>ui_flutter/protocol/PROTOCOL.md</c>;
/// the Dart mirror of this file is <c>ui_flutter/fd_ipc/lib/protocol.dart</c>. Change all three together.
/// </summary>
public static class Protocol
{
    /// <summary>Bumped when a message changes incompatibly; <c>hello</c> refuses a different value.</summary>
    public const int Version = 1;
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    /// <summary>The longest a Unix socket path may be on Windows (sun_path is 108 bytes).</summary>
    public const int MaxSocketPathChars = 100;

    public static class Methods
    {
        public const string Hello = "hello";
        public const string Ping = "ping";
        public const string Subscribe = "subscribe";
        public const string EngineQuit = "engine.quit";
        public const string SettingsGet = "settings.get";
        public const string SettingsSet = "settings.set";
        public const string PinnedGet = "pinned.get";
        public const string WindowsList = "windows.list";
        public const string MediaGet = "media.get";
        public const string MediaControl = "media.control";
    }

    public static class Events
    {
        public const string SettingsChanged = "settings.changed";
        public const string PinnedChanged = "pinned.changed";
        public const string WindowsChanged = "windows.changed";
    }

    public static class Errors
    {
        public const string BadRequest = "bad-request";
        public const string UnknownMethod = "unknown-method";
        public const string BadParams = "bad-params";
        public const string HelloRequired = "hello-required";
        public const string ProtocolMismatch = "protocol-mismatch";
        public const string Failed = "failed";
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The part of an event name before the first dot: the topic a client subscribes to.</summary>
    public static string TopicOf(string eventName)
    {
        int dot = eventName.IndexOf('.');
        return dot < 0 ? eventName : eventName[..dot];
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Json);

    public static string ResponseLine(JsonNode? id, JsonNode? result) => Serialize(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = Detach(result) });

    public static string ErrorLine(JsonNode? id, string code, string message) => Serialize(new JsonObject
    {
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    });

    public static string EventLine(string name, JsonNode? data) => Serialize(new JsonObject { ["event"] = name, ["data"] = Detach(data) });

    public static string RequestLine(long id, string method, JsonObject? p) => Serialize(new JsonObject
    {
        ["id"] = id, ["method"] = method, ["params"] = Detach(p) ?? new JsonObject(),
    });

    /// <summary>A node can only have one parent; a node that already sits in a tree is copied.</summary>
    private static JsonNode? Detach(JsonNode? node) => node?.Parent is null ? node : node.DeepClone();
}
