using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.AI;

public enum ModelTier { Lightweight, Balanced, Advanced, Unknown }

public sealed record OllamaModel(string Name, long SizeBytes, string? ParameterSize, string? Family, ModelTier Tier)
{
    public string TierLabel => Tier switch
    {
        ModelTier.Lightweight => "Lightweight",
        ModelTier.Balanced => "Balanced",
        ModelTier.Advanced => "Advanced",
        _ => "Unknown size",
    };
}

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal client for a local Ollama server. Only the read-only model list and plain chat completion are used:
/// no tools, no function calling, no model downloads (Sentinel never pulls models). The endpoint must be on this
/// computer so telemetry never leaves the machine.
/// </summary>
public sealed class OllamaClient(HttpClient http)
{
    public static bool IsLoopback(string endpoint, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        uri = u;
        return u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri Base(string endpoint)
    {
        if (!IsLoopback(endpoint, out var uri)) throw new OllamaException("Only a local Ollama endpoint (localhost) is allowed, so your telemetry never leaves this PC.");
        return uri!;
    }

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(string endpoint, CancellationToken ct)
    {
        var baseUri = Base(endpoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var resp = await http.GetFromJsonAsync(new Uri(baseUri, "/api/tags"), OllamaJson.Default.TagsResponse, cts.Token).ConfigureAwait(false);
            return (resp?.Models ?? []).Select(m => new OllamaModel(m.Name ?? "unknown", m.Size, m.Details?.ParameterSize, m.Details?.Family, Classify(m.Details?.ParameterSize, m.Size)))
                .OrderBy(m => m.Tier).ThenBy(m => m.Name).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new OllamaException("Ollama is not reachable at " + baseUri + ". Make sure it is installed and running.", ex);
        }
    }

    public async Task<string> ChatAsync(string endpoint, string model, string system, string user, CancellationToken ct)
    {
        var baseUri = Base(endpoint);
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Messages = [new ChatMessage { Role = "system", Content = system }, new ChatMessage { Role = "user", Content = user }],
            Options = new ChatOptions { Temperature = 0.2, NumPredict = 600 },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var resp = await http.PostAsJsonAsync(new Uri(baseUri, "/api/chat"), request, OllamaJson.Default.ChatRequest, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new OllamaException($"Ollama returned {(int)resp.StatusCode}. Is the model '{model}' installed?");
            var body = await resp.Content.ReadFromJsonAsync(OllamaJson.Default.ChatResponse, cts.Token).ConfigureAwait(false);
            var text = body?.Message?.Content?.Trim();
            if (string.IsNullOrEmpty(text)) throw new OllamaException("The model returned an empty answer.");
            return text.Length > 6000 ? text[..6000] : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new OllamaException(ex is TaskCanceledException ? "The local model took too long to answer." : "Ollama could not be reached.", ex);
        }
    }

    internal static ModelTier Classify(string? parameterSize, long sizeBytes)
    {
        double? billions = null;
        if (parameterSize is { Length: > 1 } p && (p.EndsWith('B') || p.EndsWith('b')) &&
            double.TryParse(p[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) billions = b;
        if (parameterSize is { Length: > 1 } m && (m.EndsWith('M') || m.EndsWith('m')) &&
            double.TryParse(m[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)) billions = mm / 1000;
        billions ??= sizeBytes > 0 ? sizeBytes / 0.6e9 : null; // rough: ~0.6 GB per billion parameters at 4-bit
        return billions switch
        {
            null => ModelTier.Unknown,
            < 4.5 => ModelTier.Lightweight,
            < 10 => ModelTier.Balanced,
            _ => ModelTier.Advanced,
        };
    }

    internal sealed class TagsResponse
    {
        [JsonPropertyName("models")] public List<TagModel>? Models { get; set; }
    }

    internal sealed class TagModel
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("details")] public TagDetails? Details { get; set; }
    }

    internal sealed class TagDetails
    {
        [JsonPropertyName("parameter_size")] public string? ParameterSize { get; set; }
        [JsonPropertyName("family")] public string? Family { get; set; }
    }

    internal sealed class ChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; set; }
        [JsonPropertyName("messages")] public required List<ChatMessage> Messages { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("options")] public ChatOptions? Options { get; set; }
    }

    internal sealed class ChatMessage
    {
        [JsonPropertyName("role")] public required string Role { get; set; }
        [JsonPropertyName("content")] public required string Content { get; set; }
    }

    internal sealed class ChatOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("num_predict")] public int NumPredict { get; set; }
    }

    internal sealed class ChatResponse
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}

[JsonSerializable(typeof(OllamaClient.TagsResponse))]
[JsonSerializable(typeof(OllamaClient.ChatRequest))]
[JsonSerializable(typeof(OllamaClient.ChatResponse))]
internal sealed partial class OllamaJson : JsonSerializerContext;
