using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Poiesis.AI.Providers;

/// <summary>
/// Fournisseur Anthropic, via le SDK officiel C# (paquet <c>Anthropic</c>), sur l'endpoint beta des messages
/// pour profiter du repli côté serveur : si le modèle décline une demande, le serveur la rejoue sur un modèle de repli.
/// </summary>
/// <remarks>
/// Réflexion adaptative (par défaut sur les modèles récents) ; les blocs de réflexion sont renvoyés tels quels
/// au tour suivant sous forme de <see cref="OpaquePart"/>, comme l'exige l'API.
/// </remarks>
public sealed class AnthropicProvider : IChatProvider
{
    const string ProviderName = "anthropic";

    readonly IAnthropicClient _client;

    public AnthropicProvider(ProviderSettings settings)
    {
        Name = settings.Name;
        Model = settings.Model;
        var apiKey = settings.ApiKeyEnvVar is { } v ? Environment.GetEnvironmentVariable(v) : null;
        _client = apiKey is null ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
        if (settings.BaseUrl is { } url)
            _client = _client.WithOptions(o => o with { BaseUrl = url });
    }

    public string Name { get; }

    public string Model { get; }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = request.MaxOutputTokens,
            System = request.System,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.High },
            Tools = request.Tools.Select(ToTool).ToList(),
            Messages = request.Messages.Select(ToMessage).ToList(),
        };

        BetaMessage response = await _client.Beta.Messages.Create(parameters, ct);

        var parts = new List<ChatPart>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out BetaTextBlock? text))
                parts.Add(new TextPart(text.Text));
            else if (block.TryPickToolUse(out BetaToolUseBlock? use))
                parts.Add(new ToolCallPart(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
            else if (block.TryPickThinking(out BetaThinkingBlock? thinking))
                parts.Add(new OpaquePart(ProviderName, new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature }));
            else if (block.TryPickRedactedThinking(out BetaRedactedThinkingBlock? redacted))
                parts.Add(new OpaquePart(ProviderName, new BetaRedactedThinkingBlockParam { Data = redacted.Data }));
            else if (block.TryPickFallback(out BetaFallbackBlock? fallback))
                parts.Add(new OpaquePart(ProviderName, ToFallbackParam(fallback)));
        }

        var stop = response.StopReason?.Raw() switch
        {
            "end_turn" or "stop_sequence" => StopReason.EndTurn,
            "tool_use" => StopReason.ToolUse,
            "max_tokens" => StopReason.MaxTokens,
            "refusal" => StopReason.Refusal,
            _ => StopReason.Other,
        };

        return new ChatResponse(
            new ChatMessage(ChatRole.Assistant, parts),
            stop,
            new Usage(response.Usage.InputTokens, response.Usage.OutputTokens),
            response.Model.Raw());
    }

    static BetaFallbackBlockParam ToFallbackParam(BetaFallbackBlock block) =>
        JsonSerializer.Deserialize<BetaFallbackBlockParam>(JsonSerializer.Serialize(block))!;

    static BetaToolUnion ToTool(ToolDefinition tool)
    {
        var props = tool.InputSchema.TryGetProperty("properties", out var p)
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
            : new Dictionary<string, JsonElement>();
        var required = tool.InputSchema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }

    static BetaMessageParam ToMessage(ChatMessage message)
    {
        var content = new List<BetaContentBlockParam>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart t:
                    content.Add(new BetaTextBlockParam { Text = t.Text });
                    break;
                case ToolCallPart c:
                    content.Add(new BetaToolUseBlockParam
                    {
                        ID = c.Id,
                        Name = c.Name,
                        Input = c.Arguments.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()),
                    });
                    break;
                case ToolResultPart res:
                    content.Add(new BetaToolResultBlockParam { ToolUseID = res.ToolCallId, Content = res.Content, IsError = res.IsError });
                    break;
                // Blocs de réflexion signés et marqueurs de repli : renvoyés à l'identique.
                case OpaquePart { Provider: ProviderName, Payload: BetaThinkingBlockParam th }:
                    content.Add(th);
                    break;
                case OpaquePart { Provider: ProviderName, Payload: BetaRedactedThinkingBlockParam rt }:
                    content.Add(rt);
                    break;
                case OpaquePart { Provider: ProviderName, Payload: BetaFallbackBlockParam fb }:
                    content.Add(fb);
                    break;
            }
        }

        return message.Role == ChatRole.User
            ? new BetaUserMessageParam { Content = content }
            : new BetaAssistantMessageParam { Content = content };
    }
}
