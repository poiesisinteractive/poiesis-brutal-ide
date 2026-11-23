using Poiesis.AI.Agent;

namespace Poiesis.AI.Features;

/// <summary>Un garde-fou : une commande qui doit réussir avant que la feature soit proposée au dev.</summary>
public sealed record ValidationGate(string Name, WorkspaceCommand Command);

public sealed record GateResult(string Name, bool Passed, string Output);

public enum FeatureStatus { Ready, GatesFailed, AgentStopped }

/// <summary>Résultat d'une feature : le diff à relire, l'état des garde-fous, et la transcription de l'agent.</summary>
public sealed record FeatureProposal(
    FeatureWorkspace Workspace,
    FeatureStatus Status,
    string Diff,
    IReadOnlyList<GateResult> Gates,
    AgentRunResult Agent);

/// <summary>
/// Le workflow « feature d'abord » : le dev décrit une fonctionnalité ; l'agent la réalise dans une branche isolée ;
/// build, tests et sim headless doivent passer (l'agent corrige tant qu'ils échouent, dans une limite d'essais) ;
/// le dev reçoit un diff qu'il accepte (fusion) ou refuse. Il peut aussi ouvrir le worktree et reprendre la main.
/// </summary>
public sealed class FeatureSession(IChatProvider provider, IReadOnlyList<ValidationGate> gates, WorkspaceCommands commands, int maxRepairRounds = 3)
{
    public event Action<AgentEvent>? AgentEvent;
    public event Action<GateResult>? GateFinished;

    public async Task<FeatureProposal> RunAsync(string repoRoot, string featureTitle, string featureDescription, string projectContext, CancellationToken ct = default)
    {
        var workspace = await FeatureWorkspace.CreateAsync(repoRoot, featureTitle, ct);
        var loop = new AgentLoop(provider, new WorkspaceTools(workspace.Path, commands).All());
        loop.Event += e => AgentEvent?.Invoke(e);

        var system = SystemPrompt(projectContext);
        IReadOnlyList<ChatMessage> history = [ChatMessage.User($"# Feature : {featureTitle}\n\n{featureDescription}")];

        AgentRunResult run;
        List<GateResult> results;
        int round = 0;
        while (true)
        {
            run = await loop.RunAsync(system, history, ct);
            if (run.Reason != StopReason.EndTurn)
            {
                await workspace.CommitAsync($"wip: {featureTitle} (agent interrompu)", ct);
                return new FeatureProposal(workspace, FeatureStatus.AgentStopped, await workspace.DiffAsync(ct), [], run);
            }

            results = await RunGatesAsync(workspace.Path, ct);
            if (results.All(r => r.Passed) || ++round > maxRepairRounds)
                break;

            // Les garde-fous ont échoué : on renvoie leurs sorties à l'agent pour qu'il corrige.
            var failures = string.Join("\n\n", results.Where(r => !r.Passed).Select(r => $"## {r.Name} a échoué\n```\n{ProcessRunner.Tail(r.Output, 6_000)}\n```"));
            history = [.. run.Transcript, ChatMessage.User($"Les vérifications ne passent pas. Corrige, puis termine.\n\n{failures}")];
        }

        await workspace.CommitAsync($"feat: {featureTitle}", ct);
        var status = results.All(r => r.Passed) ? FeatureStatus.Ready : FeatureStatus.GatesFailed;
        return new FeatureProposal(workspace, status, await workspace.DiffAsync(ct), results, run);
    }

    async Task<List<GateResult>> RunGatesAsync(string cwd, CancellationToken ct)
    {
        var results = new List<GateResult>();
        foreach (var gate in gates)
        {
            var r = await ProcessRunner.RunAsync(gate.Command.FileName, gate.Command.Args, cwd, gate.Command.Timeout, ct);
            var result = new GateResult(gate.Name, r.Succeeded, r.Output);
            GateFinished?.Invoke(result);
            results.Add(result);
            if (!r.Succeeded)
                break;   // inutile de tester si ça ne compile pas
        }
        return results;
    }

    static string SystemPrompt(string projectContext) => $"""
        Tu travailles dans l'IDE Poiesis, sur le code d'un jeu construit sur le moteur Poiesis et le framework BRUTAL (C#, .NET 10).
        Le développeur décrit une fonctionnalité ; tu la réalises de bout en bout dans une branche isolée, puis il relira ton diff.

        Comment travailler :
        - Commence par lire le code et les données qui concernent la fonctionnalité avant d'écrire.
        - Le gameplay s'écrit en composants (structs) et en systèmes (ISystem) dans l'assembly du jeu ; le contenu éditable
          (scénarios, définitions de vaisseaux et d'armes) vit dans des fichiers JSON sous content/.
        - Ne modifie pas le moteur (src/Poiesis.*) : si la fonctionnalité en a besoin, dis-le dans ton message final.
        - Ajoute ou adapte des tests pour ce que tu changes. Lance build puis test avant de terminer, et corrige ce qui échoue.
        - Si la fonctionnalité touche l'équilibrage ou les performances, lance run_headless et rapporte les métriques.
        - Termine par un résumé court : ce qui a changé, comment le vérifier dans l'éditeur, et ce qui reste ouvert.

        Contexte du projet :
        {projectContext}
        """;
}
