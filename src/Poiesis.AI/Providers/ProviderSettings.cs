namespace Poiesis.AI.Providers;

public enum ProviderKind
{
    Anthropic,
    /// <summary>API Chat Completions : OpenAI, et les serveurs compatibles (Ollama, LM Studio, Gemini).</summary>
    OpenAiCompatible,
}

/// <summary>
/// Réglages d'un fournisseur, lus depuis la configuration de l'IDE. Les clés d'API ne sont jamais
/// stockées dans le projet : seulement le nom de la variable d'environnement qui les contient.
/// </summary>
public sealed record ProviderSettings
{
    public required string Name { get; init; }

    public required ProviderKind Kind { get; init; }

    public required string Model { get; init; }

    /// <summary>URL de base ; vide pour la valeur par défaut du fournisseur.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Variable d'environnement qui contient la clé (aucune pour Ollama ou LM Studio).</summary>
    public string? ApiKeyEnvVar { get; init; }

    public static IReadOnlyList<ProviderSettings> Defaults { get; } =
    [
        new() { Name = "Anthropic", Kind = ProviderKind.Anthropic, Model = "claude-opus-5-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY" },
        new() { Name = "OpenAI", Kind = ProviderKind.OpenAiCompatible, Model = "gpt-5", BaseUrl = "https://api.openai.com/v1", ApiKeyEnvVar = "OPENAI_API_KEY" },
        new() { Name = "Gemini", Kind = ProviderKind.OpenAiCompatible, Model = "gemini-2.5-pro", BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", ApiKeyEnvVar = "GEMINI_API_KEY" },
        new() { Name = "Ollama", Kind = ProviderKind.OpenAiCompatible, Model = "qwen2.5-coder:32b", BaseUrl = "http://localhost:11434/v1" },
        new() { Name = "LM Studio", Kind = ProviderKind.OpenAiCompatible, Model = "local-model", BaseUrl = "http://localhost:1234/v1" },
    ];

    public IChatProvider Create(HttpClient? http = null) => Kind switch
    {
        ProviderKind.Anthropic => new AnthropicProvider(this),
        ProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(this, http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) }),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };
}
