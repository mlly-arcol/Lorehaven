using System.Text.Json.Serialization;

namespace NativeTavern.Models;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("messages")] public IReadOnlyList<ChatCompletionMessage> Messages { get; init; } = [];
    [JsonPropertyName("temperature")] public double Temperature { get; init; } = 0.8;
    [JsonPropertyName("top_p")] public double TopP { get; init; } = 1.0;
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; } = 1024;
    [JsonPropertyName("stream")] public bool Stream { get; init; } = true;
    [JsonIgnore] public bool DisableLocalThinking { get; init; }
}

public sealed class ChatCompletionMessage
{
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonIgnore] public string Content { get; init; } = string.Empty;
    [JsonIgnore] public long? SourceMessageId { get; init; }
    [JsonIgnore] public IReadOnlyList<string> ImageDataUrls { get; init; } = [];
    [JsonPropertyName("content")]
    public object ContentPayload => ImageDataUrls.Count == 0
        ? Content
        : new object[] { new { type = "text", text = Content } }
            .Concat(ImageDataUrls.Select(url => (object)new { type = "image_url", image_url = new { url } }))
            .ToArray();
}
