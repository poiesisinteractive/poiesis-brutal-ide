using System.Collections.Concurrent;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.AI;
using Poiesis.AI.Agent;
using Poiesis.AI.Providers;

namespace Poiesis.Editor.Ui;

/// <summary>
/// L'onglet « Scène » du panneau IA : on demande une modification de la scène ouverte (« place trois escadres en
/// tenaille autour des Clans ») ; l'agent la prépare sur une copie, l'éditeur la montre en fantôme dans le viewport, et
/// on applique les propositions cochées en une seule commande annulable, ou on refuse.
/// </summary>
sealed class SceneAgentTab(EditorSession session, LogBuffer log, Func<ProviderSettings> provider)
{
    enum State { Idle, Running, Review }

    State _state = State.Idle;
    readonly ImInputString _request = Widgets.Multiline("scene-ai-request", 2048);
    readonly ConcurrentQueue<(float4 Color, string Text)> _incoming = new();
    readonly List<(float4 Color, string Text)> _activity = new();
    Task<AgentRunResult>? _task;
    CancellationTokenSource? _cts;
    ScenePlan? _plan;
    string? _lastText;
    string? _error;

    public bool IsRunning => _state == State.Running;

    public bool IsReviewing => _state == State.Review;

    /// <summary>À chaque image, que l'onglet soit visible ou non : relève la fin de l'agent.</summary>
    public void Update()
    {
        while (_incoming.TryDequeue(out var line))
            _activity.Add(line);
        if (_state == State.Running && _task is { IsCompleted: true })
            Finish();
        // La proposition a été oubliée ailleurs (rechargement du jeu, autre scène ouverte).
        if (_state == State.Review && session.Plan is null)
            _state = State.Idle;
    }

    public void Draw()
    {
        switch (_state)
        {
            case State.Idle:
                ImGui.TextDisabled("Demandez une modification de la scène ouverte ; vous verrez la proposition avant de l'appliquer."u8);
                ImGui.InputTextMultiline("##scene-request"u8, _request, new float2(-1, ImGui.GetTextLineHeight() * 4));
                ImGui.TextDisabled(session.SelectedNodes.Count > 0 ? $"Sélection transmise : {string.Join(", ", session.SelectedNodes.Select(n => n.Name))}" : "Aucune sélection.");
                ImGui.BeginDisabled(_request.ToString().Trim().Length == 0 || session.Modules.Module is null || session.Play is not null);
                if (ImGui.Button("Proposer"u8, new float2(120, 0)))
                    Start(_request.ToString().Trim());
                ImGui.EndDisabled();
                if (_error is not null)
                    ImGui.TextColored(EditorTheme.Error, _error);
                break;
            case State.Running:
                ImGui.TextColored(EditorTheme.Accent, "L'agent prépare sa proposition…"u8);
                ImGui.SameLine();
                if (ImGui.SmallButton("Arrêter"u8))
                    _cts?.Cancel();
                break;
            case State.Review:
                DrawReview();
                break;
        }
        DrawActivity();
    }

    void DrawActivity()
    {
        if (_activity.Count == 0)
            return;
        ImGui.Separator();
        if (ImGui.BeginChild("scene-activity"u8))
        {
            foreach (var (color, text) in _activity)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped(text);
                ImGui.PopStyleColor();
            }
            if (_state == State.Running && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    public void Start(string request)
    {
        _error = null;
        _activity.Clear();
        _lastText = null;
        IChatProvider chat;
        try
        {
            chat = provider().Create();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return;
        }
        _plan = new ScenePlan(session);
        var loop = new AgentLoop(chat, _plan.Tools(), maxTurns: 40);
        loop.Event += OnEvent;
        _cts = new CancellationTokenSource();
        var system = _plan.SystemPrompt();
        var ct = _cts.Token;
        _task = Task.Run(() => loop.RunAsync(system, [ChatMessage.User(request)], ct), ct);
        _state = State.Running;
        log.Info("ia", $"Demande sur la scène, avec {provider().Name} : {request}");
    }

    void OnEvent(AgentEvent e)
    {
        switch (e)
        {
            case AssistantText t:
                _lastText = t.Text;
                _incoming.Enqueue((new float4(0.9f, 0.9f, 0.92f, 1), t.Text));
                break;
            case ToolInvoked t:
                _incoming.Enqueue((EditorTheme.Muted, $"→ {t.Name} {Short(t.Arguments)}"));
                break;
            case ToolFinished { Outcome.IsError: true } t:
                _incoming.Enqueue((EditorTheme.Warning, $"  ✗ {Short(t.Outcome.Content)}"));
                break;
            case AgentStopped s when s.Reason != StopReason.EndTurn:
                _incoming.Enqueue((EditorTheme.Warning, $"Arrêt : {s.Reason} {s.Detail}"));
                break;
        }
    }

    static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;

    void Finish()
    {
        var task = _task!;
        _task = null;
        if (!task.IsCompletedSuccessfully)
        {
            _error = task.Exception?.InnerException?.Message ?? "agent arrêté";
            log.Error("ia", $"Demande sur la scène : {_error}");
            _plan = null;
            _state = State.Idle;
            return;
        }
        _plan!.Message = _lastText;
        if (_plan.Ops.Count == 0)
        {
            log.Info("ia", "L'agent ne propose aucune modification.");
            _plan = null;
            _state = State.Idle;
            return;
        }
        session.SetPlan(_plan);
        _plan = null;
        _state = State.Review;
        log.Info("ia", $"Proposition sur la scène : {session.Plan!.Ops.Count} modification(s), en fantôme dans le viewport.");
    }

    void DrawReview()
    {
        if (session.Plan is not { } plan)
            return;
        if (plan.Message is { } message)
            ImGui.TextWrapped(message);
        ImGui.SeparatorText($"{plan.Ops.Count} modification(s) proposée(s)");
        if (ImGui.BeginChild("ops"u8, new float2(0, ImGui.GetTextLineHeightWithSpacing() * Math.Min(plan.Ops.Count + 1, 10)), ImGuiChildFlags.Borders))
        {
            for (int i = 0; i < plan.Ops.Count; i++)
            {
                var op = plan.Ops[i];
                bool on = session.PlanChosen.Contains(op);
                ImGui.PushID(i);
                if (ImGui.Checkbox("##on"u8, ref on))
                {
                    if (on)
                        session.PlanChosen.Add(op);
                    else
                        session.PlanChosen.Remove(op);
                }
                ImGui.SameLine();
                ImGui.TextWrapped(op.Summary);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        if (ImGui.Button($"Appliquer ({session.PlanChosen.Count})") && session.PlanChosen.Count > 0)
            Apply();
        ImGui.SameLine();
        if (ImGui.Button("Refuser"u8))
            Reject();
        ImGui.SameLine();
        ImGui.TextDisabled("Une seule annulation (Ctrl+Z) défait tout ce qui est appliqué."u8);
    }

    public void Apply()
    {
        if (session.Plan is null)
            return;
        int count = session.PlanChosen.Count;
        var skipped = session.ApplyPlan();
        log.Info("ia", $"Proposition appliquée : {count - skipped.Count} modification(s), annulables d'un coup.");
        foreach (var s in skipped)
            log.Warn("ia", $"Sautée (son nœud n'existe pas) : {s}");
        _state = State.Idle;
    }

    public void Reject()
    {
        session.SetPlan(null);
        log.Info("ia", "Proposition refusée.");
        _state = State.Idle;
    }
}
