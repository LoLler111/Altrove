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
[JsonDerivedType(typeof(SettingsMessage), "settings")]
[JsonDerivedType(typeof(ConfigureMessage), "configure")]
[JsonDerivedType(typeof(TouchMessage), "touch")]
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
        ["hello", "start", "stop", "keyframe", "ping", "pong", "stats", "bye", "settings", "configure", "touch"];
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

/// <summary>Optional abilities a peer lists in its hello (docs/PROTOCOLLO.md, "features").</summary>
public static class Features
{
    /// <summary>The sender accepts touch messages.</summary>
    public const string Touch = "touch";

    /// <summary>The sender sends settings and accepts configure.</summary>
    public const string Configure = "configure";
}

public static class TouchStates
{
    public const string Down = "down";
    public const string Move = "move";
    public const string Up = "up";
    public const string Cancel = "cancel";
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
    int? MaxHeight = null,
    IReadOnlyList<string>? Features = null) : ControlMessage
{
    public bool Supports(string feature) => Features?.Contains(feature) == true;
}

public sealed record StartMessage(string Codec, int Width, int Height, int Fps, string Mode) : ControlMessage;

public sealed record StopMessage : ControlMessage;

public sealed record KeyframeMessage : ControlMessage;

public sealed record PingMessage(long T) : ControlMessage;

public sealed record PongMessage(long T) : ControlMessage;

public sealed record StatsMessage(double Fps, double? DecodeMs = null, int? Dropped = null) : ControlMessage;

public sealed record ByeMessage(string Reason) : ControlMessage;

public sealed record ScreenChoice(string Id, string Name);

/// <summary>What a receiver may ask for in <see cref="ConfigureMessage"/>.</summary>
public sealed record SettingsChoices(
    IReadOnlyList<string> Modes,
    IReadOnlyList<ScreenChoice> Screens,
    IReadOnlyList<int> MaxHeights,
    IReadOnlyList<int> Fps,
    IReadOnlyList<int> BitratesKbps);

/// <summary>What is being streamed, and what the receiver can change. <c>MaxHeight</c> 0 means automatic.</summary>
public sealed record SettingsMessage(
    string Mode,
    string? Screen,
    int MaxHeight,
    int Fps,
    int BitrateKbps,
    SettingsChoices Choices,
    string? Notice = null) : ControlMessage;

/// <summary>A receiver's request to change some settings; null fields stay as they are.</summary>
public sealed record ConfigureMessage(
    string? Mode = null,
    string? Screen = null,
    int? MaxHeight = null,
    int? Fps = null,
    int? BitrateKbps = null) : ControlMessage;

/// <summary>One finger on the receiver's screen, at a position on the streamed image from 0 to 1.</summary>
public sealed record TouchPointer(int Id, double X, double Y, string State);

/// <summary>Every finger touching the receiver right now, sent at each change.</summary>
public sealed record TouchMessage(IReadOnlyList<TouchPointer> Pointers) : ControlMessage;
