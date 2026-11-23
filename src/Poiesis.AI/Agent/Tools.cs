using System.Text.Json;

namespace Poiesis.AI.Agent;

public sealed record ToolOutcome(string Content, bool IsError = false)
{
    public static ToolOutcome Error(string message) => new(message, IsError: true);
}

/// <summary>Outil appelable par le modèle. Les arguments arrivent en JSON non validé : l'outil les vérifie.</summary>
public interface IAgentTool
{
    ToolDefinition Definition { get; }

    Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}

/// <summary>Outil défini par un délégué, pratique pour les outils simples et les tests.</summary>
public sealed class DelegateTool(ToolDefinition definition, Func<JsonElement, CancellationToken, Task<ToolOutcome>> execute) : IAgentTool
{
    public ToolDefinition Definition { get; } = definition;

    public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct) => execute(arguments, ct);

    /// <summary>Schéma JSON d'un objet dont toutes les propriétés sont des chaînes requises.</summary>
    public static JsonElement StringArgs(params (string Name, string Description)[] args) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = args.ToDictionary(a => a.Name, a => (object)new { type = "string", description = a.Description }),
            ["required"] = args.Select(a => a.Name).ToArray(),
            ["additionalProperties"] = false,
        });

    public static bool TryGetString(JsonElement args, string name, out string value)
    {
        value = "";
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString()!;
        return true;
    }
}
