namespace Poiesis.AI.Agent;

/// <summary>Ce que l'agent fait, au fil de l'eau, pour l'afficher dans le panneau IA de l'éditeur.</summary>
public abstract record AgentEvent;

public sealed record AssistantText(string Text) : AgentEvent;

public sealed record ToolInvoked(string Name, string Arguments) : AgentEvent;

public sealed record ToolFinished(string Name, ToolOutcome Outcome) : AgentEvent;

public sealed record AgentStopped(StopReason Reason, string? Detail) : AgentEvent;

public sealed record AgentRunResult(StopReason Reason, IReadOnlyList<ChatMessage> Transcript, Usage Usage, int Turns);

/// <summary>
/// Boucle d'agent indépendante du fournisseur : on appelle le modèle, on exécute ses appels d'outils,
/// on renvoie tous les résultats dans un seul message, et on recommence jusqu'à la fin du tour.
/// </summary>
public sealed class AgentLoop(IChatProvider provider, IReadOnlyList<IAgentTool> tools, int maxTurns = 60)
{
    readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Definition.Name);

    public event Action<AgentEvent>? Event;

    public async Task<AgentRunResult> RunAsync(string system, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        var transcript = history.ToList();
        var definitions = tools.Select(t => t.Definition).ToList();
        long inputTokens = 0, outputTokens = 0;

        for (int turn = 1; turn <= maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await provider.CompleteAsync(new ChatRequest(system, transcript.ToArray(), definitions), ct);
            inputTokens += response.Usage.InputTokens;
            outputTokens += response.Usage.OutputTokens;
            transcript.Add(response.Message);

            if (response.Message.Text is { Length: > 0 } text)
                Event?.Invoke(new AssistantText(text));

            var calls = response.Message.Parts.OfType<ToolCallPart>().ToList();

            // Vérifier la raison d'arrêt avant d'exécuter quoi que ce soit : un appel tronqué ou refusé ne s'exécute pas.
            if (response.StopReason is StopReason.Refusal or StopReason.MaxTokens || calls.Count == 0)
            {
                var detail = response.StopReason switch
                {
                    StopReason.Refusal => "Le modèle a décliné la demande.",
                    StopReason.MaxTokens => "Réponse tronquée (limite de jetons).",
                    _ => null,
                };
                Event?.Invoke(new AgentStopped(response.StopReason, detail));
                return new AgentRunResult(response.StopReason, transcript, new Usage(inputTokens, outputTokens), turn);
            }

            var results = new List<ChatPart>(calls.Count);
            foreach (var call in calls)
            {
                Event?.Invoke(new ToolInvoked(call.Name, call.Arguments.GetRawText()));
                ToolOutcome outcome;
                if (!_tools.TryGetValue(call.Name, out var tool))
                {
                    outcome = ToolOutcome.Error($"Outil inconnu : {call.Name}");
                }
                else
                {
                    try
                    {
                        outcome = await tool.ExecuteAsync(call.Arguments, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        outcome = ToolOutcome.Error($"{ex.GetType().Name} : {ex.Message}");
                    }
                }
                Event?.Invoke(new ToolFinished(call.Name, outcome));
                results.Add(new ToolResultPart(call.Id, outcome.Content, outcome.IsError));
            }
            transcript.Add(new ChatMessage(ChatRole.User, results));
        }

        Event?.Invoke(new AgentStopped(StopReason.Other, $"Arrêt après {maxTurns} tours."));
        return new AgentRunResult(StopReason.Other, transcript, new Usage(inputTokens, outputTokens), maxTurns);
    }
}
