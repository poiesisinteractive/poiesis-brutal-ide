using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Poiesis.AI.Providers;

/// <summary>
/// Fournisseur pour l'API Chat Completions avec appels d'outils : OpenAI, et les serveurs qui l'imitent
/// (Ollama, LM Studio, et la passerelle compatible OpenAI de Gemini). Une seule implémentation HTTP couvre
/// donc quatre fournisseurs ; seuls l'URL, le modèle et la clé changent.
/// </summary>
/// <remarks>
/// Les modèles locaux suivent mal les schémas d'outils complexes : la boucle d'agent valide chaque appel
/// et renvoie l'erreur au modèle plutôt que de planter.
/// </remarks>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    readonly HttpClient _http;
    readonly Uri _endpoint;
    readonly string? _apiKey;

    public OpenAiCompatibleProvider(ProviderSettings settings, HttpClient http)
    {
        Name = settings.Name;
        Model = settings.Model;
        _http = http;
        var baseUrl = (settings.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        _endpoint = new Uri(baseUrl + "/chat/completions");
        _apiKey = settings.ApiKeyEnvVar is { } v ? Environment.GetEnvironmentVariable(v) : null;
    }

    public string Name { get; }

    public string Model { get; }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(BuildBody(request)),
        };
        if (_apiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{Name} : HTTP {(int)response.StatusCode} — {body}", null, response.StatusCode);

        return ParseResponse(JsonNode.Parse(body)!.AsObject(), Model);
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });

        foreach (var m in request.Messages)
        {
            // Les résultats d'outils sont des messages « tool » séparés dans cette API.
            foreach (var r in m.Parts.OfType<ToolResultPart>())
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.ToolCallId, ["content"] = r.IsError ? "ERREUR : " + r.Content : r.Content });

            var text = m.Text;
            var calls = m.Parts.OfType<ToolCallPart>().ToList();
            if (text.Length == 0 && calls.Count == 0)
                continue;

            var obj = new JsonObject
            {
                ["role"] = m.Role == ChatRole.User ? "user" : "assistant",
                ["content"] = text.Length > 0 ? text : null,
            };
            if (calls.Count > 0)
            {
                obj["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.GetRawText() },
                }).ToArray());
            }
            messages.Add(obj);
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["max_completion_tokens"] = request.MaxOutputTokens,
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                },
            }).ToArray());
        }
        return body;
    }

    internal static ChatResponse ParseResponse(JsonObject root, string requestedModel)
    {
        var choice = root["choices"]![0]!.AsObject();
        var msg = choice["message"]!.AsObject();
        var parts = new List<ChatPart>();

        if (msg["content"]?.GetValue<string>() is { Length: > 0 } content)
            parts.Add(new TextPart(content));

        if (msg["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                var fn = call!["function"]!;
                var rawArgs = fn["arguments"]?.GetValue<string>() ?? "{}";
                JsonElement args;
                try
                {
                    args = JsonDocument.Parse(rawArgs).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Arguments invalides : on les transmet tels quels, la boucle d'agent renverra l'erreur au modèle.
                    args = JsonSerializer.SerializeToElement(new { _invalid_json = rawArgs });
                }
                parts.Add(new ToolCallPart(call["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), fn["name"]!.GetValue<string>(), args));
            }
        }

        var stop = choice["finish_reason"]?.GetValue<string>() switch
        {
            "stop" => StopReason.EndTurn,
            "tool_calls" => StopReason.ToolUse,
            "length" => StopReason.MaxTokens,
            "content_filter" => StopReason.Refusal,
            _ => parts.OfType<ToolCallPart>().Any() ? StopReason.ToolUse : StopReason.Other,
        };

        var usage = root["usage"];
        return new ChatResponse(
            new ChatMessage(ChatRole.Assistant, parts),
            stop,
            new Usage(usage?["prompt_tokens"]?.GetValue<long>() ?? 0, usage?["completion_tokens"]?.GetValue<long>() ?? 0),
            root["model"]?.GetValue<string>() ?? requestedModel);
    }
}
