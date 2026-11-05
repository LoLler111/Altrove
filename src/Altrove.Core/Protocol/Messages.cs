using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Altrove.Core.Protocol;

/// <summary>Constants shared by every Altrove sender and receiver (docs/PROTOCOLLO.md).</summary>
public static class AltroveProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 47800;
    public const string ServiceType = "_altrove._tcp";
}

public sealed record DisplayInfo(int Width, int Height, double RefreshRate);

/// <summary>A control message: one JSON text frame with a "type" field.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(StartMessage), "start")]
[JsonDerivedType(typeof(StopMessage), "stop")]
[JsonDerivedType(typeof(KeyframeMessage), "keyframe")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(StatsMessage), "stats")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
public abstract record ControlMessage
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Other peers (Kotlin, JavaScript) may not put "type" first.
        AllowOutOfOrderMetadataProperties = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Parses a control message. Returns null for a message type this version does not know, so a newer
    /// peer can add messages without breaking an older one; throws <see cref="JsonException"/> for text that is not a message at all.
    /// </summary>
    public static ControlMessage? Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Not a JSON object.");
        if (node["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
            throw new JsonException("Missing \"type\".");
        if (!KnownTypes.Contains(type))
            return null;
        return node.Deserialize<ControlMessage>(Options);
    }

    private static readonly HashSet<string> KnownTypes =
        ["hello", "start", "stop", "keyframe", "ping", "pong", "stats", "bye"];
}

public static class Roles
{
    public const string Sender = "sender";
    public const string Receiver = "receiver";
}

public static class StreamModes
{
    public const string Mirror = "mirror";
    public const string Extend = "extend";
}

public static class ByeReasons
{
    public const string User = "user";
    public const string Replaced = "replaced";
    public const string Protocol = "protocol";
    public const string Error = "error";
}

public sealed record HelloMessage(
    int Protocol,
    string Role,
    string Id,
    string Name,
    string? App = null,
    DisplayInfo? Display = null,
    IReadOnlyList<string>? Codecs = null,
    int? MaxWidth = null,
    int? MaxHeight = null) : ControlMessage;

public sealed record StartMessage(string Codec, int Width, int Height, int Fps, string Mode) : ControlMessage;

public sealed record StopMessage : ControlMessage;

public sealed record KeyframeMessage : ControlMessage;

public sealed record PingMessage(long T) : ControlMessage;

public sealed record PongMessage(long T) : ControlMessage;

public sealed record StatsMessage(double Fps, double? DecodeMs = null, int? Dropped = null) : ControlMessage;

public sealed record ByeMessage(string Reason) : ControlMessage;
