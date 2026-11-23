using System.Text.Json;

namespace Poiesis.AI;

/// <summary>
/// Conversation neutre vis-à-vis du fournisseur. Chaque fournisseur la traduit vers son API
/// (Messages d'Anthropic, Chat Completions d'OpenAI et des serveurs compatibles).
/// </summary>
public enum ChatRole { User, Assistant }

public abstract record ChatPart;

public sealed record TextPart(string Text) : ChatPart;

/// <summary>Appel d'outil demandé par le modèle. <see cref="Arguments"/> est un objet JSON.</summary>
public sealed record ToolCallPart(string Id, string Name, JsonElement Arguments) : ChatPart;

public sealed record ToolResultPart(string ToolCallId, string Content, bool IsError = false) : ChatPart;

/// <summary>
/// Partie propre à un fournisseur, à renvoyer telle quelle au tour suivant
/// (ex. blocs de réflexion signés d'Anthropic). Les autres fournisseurs l'ignorent.
/// </summary>
public sealed record OpaquePart(string Provider, object Payload) : ChatPart;

public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ChatPart> Parts)
{
    public static ChatMessage User(string text) => new(ChatRole.User, [new TextPart(text)]);

    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>Outil exposé au modèle : nom, description et schéma JSON des arguments.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record ChatRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    int MaxOutputTokens = 16_000);

public enum StopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    /// <summary>Le modèle a décliné la demande (filtre de sécurité du fournisseur).</summary>
    Refusal,
    Other,
}

public sealed record Usage(long InputTokens, long OutputTokens);

public sealed record ChatResponse(ChatMessage Message, StopReason StopReason, Usage Usage, string Model);

/// <summary>Un fournisseur de modèles de langage capable d'appels d'outils.</summary>
public interface IChatProvider
{
    /// <summary>Nom affiché (« Anthropic », « OpenAI », « Ollama »…).</summary>
    string Name { get; }

    string Model { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}
