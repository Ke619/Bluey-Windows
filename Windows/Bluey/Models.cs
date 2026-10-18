using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bluey;

/// What the phone should show right now. Field names match the iPhone app's FaceState exactly.
internal sealed class FaceState
{
    [JsonPropertyName("gazeX")] public double GazeX { get; set; }
    [JsonPropertyName("gazeY")] public double GazeY { get; set; }
    /// listening, resting, thinking, talking, pointing, happy, sleepy
    [JsonPropertyName("mood")] public string Mood { get; set; } = "listening";
    [JsonPropertyName("talk")] public double Talk { get; set; }
}

/// One line of JSON between the PC and the phone. Field names match the iPhone app's Packet.
internal sealed class Packet
{
    [JsonPropertyName("face")] public FaceState? Face { get; set; }
    [JsonPropertyName("hello")] public string? Hello { get; set; }
    [JsonPropertyName("volume")] public double? Volume { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("audio")] public string? Audio { get; set; }
    [JsonPropertyName("speech")] public int? Speech { get; set; }
    [JsonPropertyName("callID")] public string? CallID { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
