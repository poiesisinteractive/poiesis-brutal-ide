using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.AI;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>
/// Analyse de parties : une série jouée en arrière-plan sur plusieurs graines (victoires, durées, mesures du jeu),
/// ou le bilan de la partie en cours ; puis une question à l'IA, qui reçoit ces chiffres et les données du jeu.
/// Sa réponse peut devenir une demande de feature dans le panneau IA.
/// </summary>
sealed class AnalysisPanel(EditorSession session, ProjectConfig project, LogBuffer log, AiPanel ai)
{
    int _runs = 20, _firstSeed = 1, _maxMinutes = 20;
    Task<SeriesResult>? _series;
    CancellationTokenSource? _cts;
    int _done;
    SeriesResult? _result;
    string? _error;

    Task<string>? _question;
    string? _answer;
    readonly ImInputString _prompt = Widgets.Multiline("analysis-question", 4096);
    bool _promptInitialized;

    public bool IsRunning => _series is { IsCompleted: false };

    public void Draw()
    {
        Drain();
        if (ImGui.Begin("Analyse"u8))
        {
            if (session.Modules.Module is not IGameReporter)
            {
                ImGui.TextDisabled("Le jeu ne fournit pas de bilan de partie (IGameReporter)."u8);
                ImGui.End();
                return;
            }
            DrawSeries();
            ImGui.Spacing();
            DrawQuestion();
        }
        ImGui.End();
    }

    void DrawSeries()
    {
        ImGui.SeparatorText("Série de parties"u8);
        ImGui.SetNextItemWidth(90);
        ImGui.DragInt("parties"u8, ref _runs, 0.2f, 1, 500);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.DragInt("1re graine"u8, ref _firstSeed, 0.2f, 0, int.MaxValue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.DragInt("min max"u8, ref _maxMinutes, 0.2f, 1, 240);

        if (IsRunning)
        {
            ImGui.ProgressBar(_done / (float)_runs, new float2(-80, 0), $"{_done}/{_runs}");
            ImGui.SameLine();
            if (ImGui.Button("Arrêter"u8))
                _cts?.Cancel();
        }
        else if (ImGui.Button("Jouer la série sur la scène ouverte"u8))
        {
            StartSeries();
        }
        if (_error is not null)
            ImGui.TextColored(EditorTheme.Error, _error);

        if (_result is { } r)
        {
            ImGui.Text($"{r.Runs.Count} parties · " + string.Join(" · ", r.WinRates.OrderByDescending(w => w.Value).Select(w => $"{w.Key} {w.Value:P0}")));
            if (ImGui.BeginTable("stats"u8, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Mesure"u8);
                ImGui.TableSetupColumn("Moyenne"u8);
                ImGui.TableSetupColumn("Écart type"u8);
                ImGui.TableSetupColumn("Min – max"u8);
                ImGui.TableHeadersRow();
                foreach (var s in r.Stats)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(s.Name);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{s.Mean:0.##}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{s.StdDev:0.##}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{s.Min:0.##} – {s.Max:0.##}");
                }
                ImGui.EndTable();
            }
        }
        else if (session.Play is not null && session.Modules.Module is IGameReporter reporter)
        {
            var report = reporter.Report(session.Play.World);
            ImGui.TextDisabled($"Partie en cours : {(report.Finished ? $"victoire {report.Winner}" : "en cours")} à {report.Time:0} s");
        }
    }

    void DrawQuestion()
    {
        ImGui.SeparatorText("Demander à l'IA"u8);
        if (!_promptInitialized)
        {
            _prompt.SetValue("Comment expliques-tu ce résultat ? L'équilibrage est-il sain ? Propose des changements précis dans les données (fichiers et champs), du plus efficace au moins risqué.".AsSpan());
            _promptInitialized = true;
        }
        ImGui.InputTextMultiline("##question"u8, _prompt, new float2(-1, ImGui.GetTextLineHeight() * 3.5f));
        var source = _result is not null ? "la série" : session.Play is not null ? "la partie en cours" : null;
        ImGui.BeginDisabled(source is null || _question is { IsCompleted: false });
        if (ImGui.Button(source is null ? "Jouez une série ou une partie d'abord" : $"Analyser {source} avec {ai.Provider.Name}"))
            Ask();
        ImGui.EndDisabled();
        if (_question is { IsCompleted: false })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("analyse en cours…"u8);
        }

        if (_answer is not null)
        {
            if (ImGui.BeginChild("answer"u8, new float2(0, -30), ImGuiChildFlags.Borders))
                ImGui.TextWrapped(_answer);
            ImGui.EndChild();
            if (ImGui.Button("En faire une feature (panneau IA)"u8))
                ai.Draft("Équilibrage d'après l'analyse", "Applique les changements de données suivants, issus de l'analyse d'une série de batailles. "
                    + "Ne change que les fichiers de contenu, garde les tests verts, et vérifie l'effet avec run_headless.\n\n" + _answer);
            ImGui.SameLine();
            if (ImGui.Button("Copier"u8))
                ImGui.SetClipboardText(_answer);
        }
    }

    /// <summary>Script de démonstration : lance une série de <paramref name="runs"/> parties.</summary>
    public void ScriptStart(int runs)
    {
        _runs = runs;
        StartSeries();
    }

    /// <summary>Script de démonstration : pose la question à l'IA dès que la série est finie.</summary>
    public void ScriptAsk(string provider)
    {
        _askWhenDone = true;
    }

    bool _askWhenDone;

    void StartSeries()
    {
        _error = null;
        _result = null;
        _done = 0;
        var module = session.Modules.Module!;
        // La scène est figée au lancement : on peut continuer à éditer pendant la série.
        var json = session.Serializer.Write(session.Scene);
        var serializer = session.Serializer;
        var name = session.ScenePath is { } p ? Path.GetFileName(p) : session.Scene.Name;
        int runs = _runs, seed = _firstSeed;
        double max = _maxMinutes * 60;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        log.Info("analyse", $"Série de {runs} parties sur {name}…");
        _series = Task.Run(() => Series.Run(name, () => serializer.Read(json), module, seed, runs, max, (i, _) => Interlocked.Exchange(ref _done, i + 1), ct), ct);
    }

    void Ask()
    {
        var data = _result?.Summary() ?? CurrentGameSummary();
        var question = _prompt.ToString();
        var provider = ai.Provider;
        log.Info("analyse", $"Analyse demandée à {provider.Name}.");
        _answer = null;
        _question = Task.Run(async () =>
        {
            var chat = provider.Create();
            var request = new ChatRequest(
                """
                Tu es game designer et analyste de simulation pour un jeu de combat de flottes spatiales. On te donne le bilan
                chiffré de parties jouées par la simulation (déterministe, sans joueur), et les données des vaisseaux du jeu.
                Réponds en français, de façon concise et concrète : explique les causes probables, chiffres à l'appui, puis
                propose des changements précis (fichier, champ, ancienne et nouvelle valeur) et comment vérifier leur effet.
                """,
                [ChatMessage.User($"{question}\n\n# Résultats\n{data}\n\n# Données du jeu\n{GameData()}")],
                [], 4000);
            var response = await chat.CompleteAsync(request);
            return response.Message.Text;
        });
    }

    string CurrentGameSummary()
    {
        if (session.Play is not { } play || session.Modules.Module is not IGameReporter reporter)
            return "";
        var r = reporter.Report(play.World);
        return $"Partie en cours sur « {session.Scene.Name} », t = {r.Time:0} s, {(r.Finished ? $"terminée, victoire {r.Winner}" : "pas terminée")}.\n"
            + string.Join("\n", r.Metrics.Select(m => $"- {m.Key} : {m.Value:0.##}"));
    }

    /// <summary>Les données du jeu (hors scènes), en JSON, pour que l'IA puisse proposer des changements précis.</summary>
    string GameData()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(project.ContentRoot, "*.json", SearchOption.AllDirectories)
                     .Where(f => !f.EndsWith(".scene.json") && !f.Contains(".poiesis")).Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"## {Path.GetRelativePath(project.Root, file)}");
            sb.AppendLine(File.ReadAllText(file));
        }
        return sb.ToString();
    }

    void Drain()
    {
        if (_series is { IsCompleted: true } s)
        {
            _series = null;
            if (s.IsCompletedSuccessfully)
            {
                _result = s.Result;
                log.Info("analyse", _result.Summary().Split('\n')[1]);
                if (_askWhenDone)
                {
                    _askWhenDone = false;
                    Ask();
                }
            }
            else
            {
                _error = s.IsCanceled ? "Série arrêtée." : s.Exception?.InnerException?.Message;
            }
        }
        if (_question is { IsCompleted: true } q)
        {
            _question = null;
            _answer = q.IsCompletedSuccessfully ? q.Result : $"Erreur : {q.Exception?.InnerException?.Message}";
            log.Info("analyse", "Analyse de l'IA reçue.");
        }
    }
}
