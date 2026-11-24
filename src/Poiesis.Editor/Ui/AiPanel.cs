using System.Collections.Concurrent;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.AI;
using Poiesis.AI.Agent;
using Poiesis.AI.Features;
using Poiesis.AI.Providers;

namespace Poiesis.Editor.Ui;

/// <summary>
/// Le panneau « feature d'abord » : on décrit une fonctionnalité, l'agent la réalise dans une branche isolée,
/// on suit ses actions et ses garde-fous, puis on relit le diff et on accepte ou on refuse.
/// Accepter fusionne la branche, recompile le jeu et le recharge à chaud.
/// </summary>
sealed class AiPanel(EditorSession session, ProjectConfig project, LogBuffer log, Action onAccepted)
{
    enum State { Idle, Running, Review }

    sealed record Line(float4 Color, string Text);

    readonly ConcurrentQueue<Line> _incoming = new();
    readonly List<Line> _activity = new();
    State _state = State.Idle;
    int _provider;
    string _title = "";
    Task<FeatureProposal>? _task;
    CancellationTokenSource? _cts;
    FeatureProposal? _proposal;
    string[] _diffLines = [];
    string? _error;
    readonly ImInputString _description = Widgets.Multiline("ai-description");

    public void Draw()
    {
        Drain();
        if (ImGui.Begin("IA"u8))
        {
            switch (_state)
            {
                case State.Idle: DrawRequest(); break;
                case State.Running: DrawRunning(); break;
                case State.Review: DrawReview(); break;
            }
        }
        ImGui.End();
    }

    void DrawRequest()
    {
        var providers = ProviderSettings.Defaults;
        var current = providers[_provider];
        if (ImGui.BeginCombo("Fournisseur"u8, $"{current.Name} · {current.Model}"))
        {
            for (int i = 0; i < providers.Count; i++)
                if (ImGui.Selectable($"{providers[i].Name} · {providers[i].Model}", i == _provider))
                    _provider = i;
            ImGui.EndCombo();
        }
        if (current.ApiKeyEnvVar is { } env && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(env)))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            ImGui.TextWrapped($"{env} n'est pas définie : ce fournisseur refusera la demande.");
            ImGui.PopStyleColor();
        }
        else if (current.ApiKeyEnvVar is null)
            ImGui.TextDisabled($"Serveur local : {current.BaseUrl ?? "?"}");

        ImGui.Spacing();
        Widgets.TextField("Titre", "ai-title", ref _title);
        ImGui.TextDisabled("Décrivez la fonctionnalité, pas le code :"u8);
        ImGui.InputTextMultiline("##description"u8, _description, new float2(-1, ImGui.GetTextLineHeight() * 8));

        if (_error is not null)
            ImGui.TextColored(EditorTheme.Error, _error);

        var git = project.FindGitRoot();
        bool ready = _title.Trim().Length > 0 && _description.ToString().Trim().Length > 0 && git is not null;
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Lancer"u8, new float2(120, 0)))
            Start(current, git!);
        ImGui.EndDisabled();
        if (git is null)
        {
            ImGui.SameLine();
            ImGui.TextColored(EditorTheme.Warning, "Le projet n'est pas dans un dépôt git."u8);
        }

        ImGui.Spacing();
        ImGui.TextWrapped("L'agent travaille dans une branche à part (worktree sous .poiesis/worktrees). Il doit compiler et passer les tests du jeu avant de vous rendre la main ; vous relisez ensuite le diff."u8);
        DrawActivity();
    }

    void DrawRunning()
    {
        ImGui.TextColored(EditorTheme.Accent, $"En cours : {_title}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Arrêter"u8))
            _cts?.Cancel();
        DrawActivity();
    }

    void DrawReview()
    {
        var p = _proposal!;
        var (label, color) = p.Status switch
        {
            FeatureStatus.Ready => ("Prête : build et tests passent", new float4(0.45f, 0.9f, 0.5f, 1)),
            FeatureStatus.GatesFailed => ("Garde-fous en échec : à relire avec prudence", EditorTheme.Warning),
            _ => ("Agent interrompu : travail partiel", EditorTheme.Warning),
        };
        ImGui.TextColored(color, label);
        ImGui.TextDisabled($"Branche {p.Workspace.Branch} · {p.Agent.Turns} tours · {p.Agent.Usage.InputTokens + p.Agent.Usage.OutputTokens} jetons");
        foreach (var g in p.Gates)
        {
            ImGui.TextColored(g.Passed ? new float4(0.45f, 0.9f, 0.5f, 1) : EditorTheme.Error, g.Passed ? $"✓ {g.Name}" : $"✗ {g.Name}");
            ImGui.SameLine();
        }
        ImGui.NewLine();

        if (ImGui.Button("Accepter"u8, new float2(110, 0)))
            Finish(accept: true);
        ImGui.SameLine();
        if (ImGui.Button("Refuser"u8, new float2(110, 0)))
            Finish(accept: false);
        ImGui.SameLine();
        if (ImGui.Button("Copier le chemin du worktree"u8))
            ImGui.SetClipboardText(p.Workspace.Path);

        if (ImGui.BeginTabBar("review"u8))
        {
            if (ImGui.BeginTabItem("Diff"u8))
            {
                DrawDiff();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Activité"u8))
            {
                DrawActivity();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    void DrawDiff()
    {
        if (ImGui.BeginChild("diff"u8, new float2(0, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(_diffLines.Length);
            while (clipper.Step())
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var l = _diffLines[i];
                    var c = l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("diff ") ? EditorTheme.Accent
                        : l.StartsWith('+') ? new float4(0.45f, 0.9f, 0.5f, 1)
                        : l.StartsWith('-') ? EditorTheme.Error
                        : l.StartsWith("@@") ? EditorTheme.Coalition
                        : new float4(0.8f, 0.8f, 0.82f, 1);
                    ImGui.PushStyleColor(ImGuiCol.Text, c);
                    ImGui.Text(l.Length == 0 ? " " : l);
                    ImGui.PopStyleColor();
                }
            clipper.End();
        }
        ImGui.EndChild();
    }

    void DrawActivity()
    {
        if (_activity.Count == 0)
            return;
        ImGui.Separator();
        if (ImGui.BeginChild("activity"u8, new float2(0, 0), ImGuiChildFlags.None))
        {
            foreach (var line in _activity)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, line.Color);
                ImGui.TextWrapped(line.Text);
                ImGui.PopStyleColor();
            }
            if (_state == State.Running && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    void Start(ProviderSettings settings, string gitRoot)
    {
        _error = null;
        _activity.Clear();
        IChatProvider provider;
        try
        {
            provider = settings.Create();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return;
        }

        string Rel(string path) => Path.GetRelativePath(gitRoot, project.Abs(path)).Replace('\\', '/');
        var build = new WorkspaceCommand("dotnet", ["build", Rel(project.GameProject), "-nologo", "-v", "q", "-clp:ErrorsOnly"], TimeSpan.FromMinutes(5));
        var test = project.TestProject is { } t
            ? new WorkspaceCommand("dotnet", ["test", Rel(t), "-nologo", "-v", "q"], TimeSpan.FromMinutes(10))
            : build;
        var headless = (string scene) => new WorkspaceCommand("dotnet",
            ["run", "--project", "src/Poiesis.Headless", "-c", "Release", "--", Rel(project.GameAssembly).Replace("/Debug/", "/Release/"), Rel("content"), scene],
            TimeSpan.FromMinutes(5));
        var commands = new WorkspaceCommands(build, test, headless);
        var gates = new List<ValidationGate> { new("build", build) };
        if (project.TestProject is not null)
            gates.Add(new ValidationGate("tests", test));

        var session = new FeatureSession(provider, gates, commands);
        session.AgentEvent += OnAgentEvent;
        session.GateFinished += g => _incoming.Enqueue(new Line(g.Passed ? new float4(0.45f, 0.9f, 0.5f, 1) : EditorTheme.Error,
            g.Passed ? $"✓ garde-fou {g.Name}" : $"✗ garde-fou {g.Name}\n{ProcessRunner.Tail(g.Output, 1500)}"));

        var title = _title.Trim();
        var description = _description.ToString().Trim();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _state = State.Running;
        log.Info("ia", $"Feature « {title} » lancée avec {settings.Name} ({settings.Model})");
        _incoming.Enqueue(new Line(EditorTheme.Muted, $"Branche feature/{FeatureWorkspace.Slugify(title)}, fournisseur {settings.Name}."));
        _task = Task.Run(() => session.RunAsync(gitRoot, title, description, ProjectContext(gitRoot), ct), ct);
    }

    void OnAgentEvent(AgentEvent e)
    {
        var line = e switch
        {
            AssistantText t => new Line(new float4(0.9f, 0.9f, 0.92f, 1), t.Text),
            ToolInvoked t => new Line(EditorTheme.Coalition, $"→ {t.Name} {Shorten(t.Arguments)}"),
            ToolFinished { Outcome.IsError: true } t => new Line(EditorTheme.Error, $"  ✗ {t.Name} : {Shorten(t.Outcome.Content, 400)}"),
            ToolFinished t => new Line(EditorTheme.Muted, $"  ✓ {t.Name}"),
            AgentStopped { Reason: StopReason.EndTurn } => new Line(EditorTheme.Muted, "Agent terminé ; vérifications en cours…"),
            AgentStopped s => new Line(EditorTheme.Warning, s.Detail ?? $"Arrêt : {s.Reason}"),
            _ => new Line(EditorTheme.Muted, e.ToString()),
        };
        _incoming.Enqueue(line);
    }

    static string Shorten(string s, int max = 160) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Appelé à chaque image : récupère les messages de l'agent et le résultat final.</summary>
    void Drain()
    {
        while (_incoming.TryDequeue(out var l))
            _activity.Add(l);

        if (_task is not { IsCompleted: true } task)
            return;
        _task = null;
        if (task.IsCompletedSuccessfully)
        {
            _proposal = task.Result;
            _diffLines = _proposal.Diff.Replace("\r", "").Split('\n');
            _state = State.Review;
            log.Info("ia", $"Feature « {_title} » prête à relire ({_proposal.Status}).");
        }
        else
        {
            _state = State.Idle;
            _error = task.IsCanceled ? "Arrêtée." : task.Exception?.InnerException?.Message;
            log.Error("ia", $"Feature « {_title} » : {_error}");
        }
    }

    void Finish(bool accept)
    {
        var p = _proposal!;
        try
        {
            if (accept)
            {
                p.Workspace.AcceptAsync().GetAwaiter().GetResult();
                log.Info("ia", $"Feature fusionnée depuis {p.Workspace.Branch}.");
                onAccepted();
            }
            else
            {
                p.Workspace.RejectAsync().GetAwaiter().GetResult();
                log.Info("ia", $"Feature refusée ; la branche {p.Workspace.Branch} est gardée.");
            }
            _state = State.Idle;
            _proposal = null;
            _activity.Clear();
            _title = "";
            _description.SetValue(ReadOnlySpan<char>.Empty);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            log.Error("ia", ex.Message);
        }
    }

    string ProjectContext(string gitRoot)
    {
        var nodes = string.Join(", ", session.NodeTypes.All.Select(t => $"{t.Name} ({t.Type.Name})"));
        var root = Path.GetRelativePath(gitRoot, project.Root).Replace('\\', '/');
        return $"""
            Jeu : {project.Name}, dossier {root}/ du dépôt.
            Gameplay (DLL rechargée à chaud) : {root}/{project.GameProject}. Tests : {root}/{project.TestProject ?? "aucun"}.
            Contenu JSON : {root}/content/ (scènes *.scene.json, définitions de vaisseaux ships/*.ship.json).
            Types de nœuds enregistrés : {nodes}.
            Le moteur (src/Poiesis.*) fournit World, ISystem, Schedule, Node, IGameModule, Transform et Renderable.
            """;
    }
}
