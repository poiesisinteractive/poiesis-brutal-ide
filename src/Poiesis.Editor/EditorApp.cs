using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Editor.Ui;
using Poiesis.Render.Vulkan;

namespace Poiesis.Editor;

/// <summary>
/// L'application éditeur : l'état (<see cref="EditorSession"/>), les panneaux, le build intégré et le splash BRUTAL.
/// Tout ce qui est visible part d'ici ; la logique testable vit dans Poiesis.Editor.Core.
/// </summary>
sealed class EditorApp : IDisposable
{
    readonly EditorOptions _options;
    readonly VulkanContext _vk;
    readonly ImGuiHost _imgui;
    readonly ProjectConfig _project;
    readonly EditorSession _session;
    readonly LogBuffer _log = new();
    readonly BuildService _build;
    readonly Exporter _exporter;
    readonly CodeEditor _code;
    readonly TestService _tests;
    readonly SceneRenderer _renderer;
    readonly ViewportPanel _viewport;
    readonly ScenePanel _scenePanel;
    readonly InspectorPanel _inspector;
    readonly ConsolePanel _console;
    readonly EntitiesPanel _entities;
    readonly DataPanel _data;
    readonly ContentPanel _content;
    readonly ExtensionHost _extensions;
    readonly AiPanel _ai;
    readonly DebugPanel _debug;
    readonly AnalysisPanel _analysis;
    readonly PropertyEditor _properties;
    readonly Splash _splash;
    readonly Queue<string> _script;
    bool _reloadAfterBuild;
    bool _showAbout;
    float _speed = 1;
    int _scriptWait = 2;   // le script attend que les fenêtres existent

    readonly Action<string> _requestCapture;

    public EditorApp(EditorOptions options, VulkanContext vk, ImGuiHost imgui, Action<string> requestCapture)
    {
        _requestCapture = requestCapture;
        _options = options;
        _vk = vk;
        _imgui = imgui;
        _project = ProjectConfig.Load(options.ProjectRoot);
        _session = new EditorSession(_project.Root);
        _session.Log += m => _log.Info("éditeur", m);
        _build = new BuildService(_project, _log);
        _exporter = new Exporter(_project, _log);
        _code = CodeEditor.Load(_project, _log);
        _tests = new TestService(_project, _log);

        _renderer = new SceneRenderer(vk, Swapchain.FramesInFlight);
        _viewport = new ViewportPanel(_session, _renderer, new ViewportTextures(imgui.Renderer, _renderer));
        _scenePanel = new ScenePanel(_session, _project, _log, RequestOpenScene);
        _properties = new PropertyEditor(_session, _project);
        _inspector = new InspectorPanel(_session, _properties, RequestOpenScene, _project.ContentRoot);
        _console = new ConsolePanel(_log, _code);
        _entities = new EntitiesPanel(_session);
        _data = new DataPanel(_project, _log, ReloadModule);
        _content = new ContentPanel(new ContentLibrary(_project), _session, _log, RequestOpenScene, _data.OpenFile);
        _ai = new AiPanel(_session, _project, _log, () => { _reloadAfterBuild = true; _build.Start(); });
        _debug = new DebugPanel(_session, _log);
        _analysis = new AnalysisPanel(_session, _project, _log, _ai);
        _viewport.DebugChannels = _debug.EnabledChannels;
        _extensions = new ExtensionHost(_session, _log, _properties);
        _viewport.Extensions = _extensions;
        _inspector.Extensions = _extensions.DrawInspectors;
        _splash = new Splash(vk, imgui.Renderer, options.SplashSeconds);
        _script = new Queue<string>(options.Script);

        _log.Info("éditeur", $"Projet {_project.Name} · GPU {vk.DeviceName}");
        var scene = options.ScenePath ?? (_project.StartScene is { } s ? _project.Abs(s) : null);
        if (scene is not null && File.Exists(scene))
            _pendingScene = scene;
        LoadModuleOrBuild();
    }

    /// <summary>Scène de départ, ouverte dès que le module du jeu est chargé (ses types de nœuds en viennent).</summary>
    string? _pendingScene;

    void OpenPendingScene()
    {
        if (_pendingScene is not { } scene || _session.Modules.Module is null)
            return;
        _pendingScene = null;
        OpenScene(scene);
    }

    void OpenScene(string path)
    {
        try
        {
            _session.OpenScene(path);
        }
        catch (KeyNotFoundException ex)
        {
            _log.Error("éditeur", $"Ouverture de la scène : {ex.Message}. La DLL du jeu est sans doute périmée : Ctrl+B la recompile et la recharge.");
        }
        catch (Exception ex)
        {
            _log.Error("éditeur", $"Ouverture de la scène : {ex.Message}");
        }
    }

    public bool QuitRequested { get; private set; }

    /// <summary>
    /// Charge la DLL du jeu si elle est à jour ; sinon (absente, ou plus vieille que les sources, par exemple après un pull)
    /// la compile d'abord, et la chargera à la fin du build.
    /// </summary>
    void LoadModuleOrBuild()
    {
        var dll = _project.Abs(_project.GameAssembly);
        if (!_project.IsGameAssemblyStale())
        {
            Try(() => _session.LoadGameModule(dll), "Chargement du module");
            OpenPendingScene();
            return;
        }
        if (File.Exists(dll))
            _log.Info("éditeur", "La DLL du jeu est plus ancienne que ses sources : compilation avant chargement.");
        _reloadAfterBuild = true;
        _build.Start();
    }

    void ReloadModule()
    {
        if (_analysis is { IsRunning: true })
        {
            _log.Warn("éditeur", "Une série de parties utilise le module : rechargement reporté. Arrêtez la série ou attendez sa fin.");
            _reloadAfterBuild = false;
            return;
        }
        var dll = _project.Abs(_project.GameAssembly);
        if (_session.Modules.Module is null)
            Try(() => _session.LoadGameModule(dll), "Chargement du module");
        else
        {
            Try(() => _session.HotReload(dll), "Rechargement à chaud");
            _leakCheckAt = DateTime.UtcNow.AddSeconds(3);
        }
        _properties.InvalidateContent();
        OpenPendingScene();
    }

    DateTime? _leakCheckAt;

    /// <summary>Quelques secondes après un rechargement, vérifie que l'ancienne assembly du jeu est bien partie.</summary>
    void CheckPreviousGeneration()
    {
        if (_leakCheckAt is not { } at || DateTime.UtcNow < at)
            return;
        _leakCheckAt = null;
        if (_session.Modules.PreviousGenerationAlive)
            _log.Warn("éditeur", "L'ancienne assembly du jeu est encore en mémoire : une référence (événement statique, thread, cache) la retient.");
    }

    void Try(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error("éditeur", $"{what} : {ex.Message}");
        }
    }

    public void Update(FrameContext frame)
    {
        if (_splash.Draw())
            return;   // le logo BRUTAL s'affiche avant l'éditeur (licence BRUTAL, section 2.2)

        if (_build.TakeCompleted() is { } result)
        {
            if (result.Succeeded && _reloadAfterBuild)
                ReloadModule();
            _reloadAfterBuild = false;
        }

        _exporter.TakeCompleted();
        _tests.TakeCompleted();
        RunScript();
        CheckPreviousGeneration();
        _session.UpdatePreview();
        _session.Update(ImGui.GetIO().DeltaTime);

        var dockspace = ImGui.DockSpaceOverViewport(default, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode);
        if (!_imgui.HasLayout && !_layoutBuilt)
            BuildDefaultLayout(dockspace);

        MainMenu();
        Shortcuts();
        _viewport.Draw(frame.Slot);
        _scenePanel.Draw();
        DockLikeIfNew("Scène");
        _content.Draw();
        _inspector.Draw();
        _console.Draw();
        _entities.Draw();
        _data.Draw();
        _ai.Draw();
        _debug.Draw();
        _analysis.Draw();
        _extensions.DrawPanels(() => DockLikeIfNew("Analyse"));
        if (_showAbout)
            About();
        UnsavedChangesPopup();
    }

    /// <summary>
    /// Un panneau apparu après l'enregistrement de la disposition (nouvelle version, extension du jeu) n'a pas de place :
    /// ImGui l'ouvrirait flottant et minuscule. On l'ancre à côté de <paramref name="sibling"/> la première fois.
    /// </summary>
    static void DockLikeIfNew(string sibling)
    {
        var window = ImGui.Internal.FindWindowByName(sibling);
        if (!window.IsNull() && window.DockId != default)
            ImGui.SetNextWindowDockID(window.DockId, ImGuiCond.FirstUseEver);
    }

    string? _openAfterConfirm;

    /// <summary>Ouvre une scène ; si la scène en cours a des modifications non enregistrées, demande d'abord.</summary>
    void RequestOpenScene(string path)
    {
        if (_session.IsDirty)
            _openAfterConfirm = path;
        else
            OpenScene(path);
    }

    void UnsavedChangesPopup()
    {
        if (_openAfterConfirm is null)
            return;
        if (!ImGui.IsPopupOpen("Modifications non enregistrées"u8))
            ImGui.OpenPopup("Modifications non enregistrées"u8);
        if (!ImGui.BeginPopupModal("Modifications non enregistrées"u8, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var name = _session.ScenePath is { } p ? Path.GetFileName(p) : "la scène";
        ImGui.Text($"{name} a des modifications non enregistrées.");
        ImGui.Text($"Ouvrir {Path.GetFileName(_openAfterConfirm)} ?");
        if (_session.ScenePath is not null)
        {
            if (ImGui.Button("Enregistrer puis ouvrir"u8))
            {
                Save();
                Close(open: true);
            }
            ImGui.SameLine();
        }
        if (ImGui.Button("Ouvrir sans enregistrer"u8))
            Close(open: true);
        ImGui.SameLine();
        if (ImGui.Button("Annuler"u8))
            Close(open: false);
        ImGui.EndPopup();

        void Close(bool open)
        {
            var path = _openAfterConfirm!;
            _openAfterConfirm = null;
            ImGui.CloseCurrentPopup();
            if (open)
                OpenScene(path);
        }
    }

    /// <summary>Le viewport 3D, rendu hors écran avant la passe de la fenêtre.</summary>
    public void RenderOffscreen(FrameContext frame)
    {
        if (_splash.Active)
            return;
        _renderer.Render(frame.Commands, frame.Slot, _viewport.BuildFrame());
    }

    // ----- Menus et barre d'outils -------------------------------------------------------------------

    void MainMenu()
    {
        if (!ImGui.BeginMainMenuBar())
            return;
        if (ImGui.BeginMenu("Fichier"u8))
        {
            if (ImGui.MenuItem("Nouvelle scène"u8))
                _session.NewScene();
            if (ImGui.BeginMenu("Ouvrir une scène"u8))
            {
                foreach (var f in Directory.EnumerateFiles(_project.ContentRoot, "*.scene.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    if (ImGui.MenuItem(Path.GetRelativePath(_project.ContentRoot, f)))
                        RequestOpenScene(f);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Enregistrer"u8, "Ctrl+S"u8, false, _session.ScenePath is not null))
                Save();
            ImGui.Separator();
            if (ImGui.MenuItem("Quitter"u8))
                QuitRequested = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Édition"u8))
        {
            if (ImGui.MenuItem(_session.History.NextUndo is { } u ? $"Annuler « {u} »" : "Annuler", "Ctrl+Z"u8, false, _session.History.CanUndo))
                _session.History.Undo();
            if (ImGui.MenuItem(_session.History.NextRedo is { } r ? $"Rétablir « {r} »" : "Rétablir", "Ctrl+Y"u8, false, _session.History.CanRedo))
                _session.History.Redo();
            ImGui.Separator();
            bool canEdit = _session.Play is null && _session.Selection is not null;
            if (ImGui.MenuItem("Copier"u8, "Ctrl+C"u8, false, canEdit))
                _scenePanel.CopySelection();
            if (ImGui.MenuItem("Couper"u8, "Ctrl+X"u8, false, canEdit && _session.Selection!.Parent is { HasEditableChildren: true }))
                _scenePanel.CutSelection();
            if (ImGui.MenuItem("Coller"u8, "Ctrl+V"u8, false, _session.Play is null))
                _scenePanel.PasteHere();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Jeu"u8))
        {
            if (ImGui.MenuItem("Compiler et recharger"u8, "Ctrl+B"u8, false, !_build.IsBuilding))
                BuildAndReload();
            if (ImGui.MenuItem("Recharger sans compiler"u8))
                ReloadModule();
            if (ImGui.MenuItem(_tests.IsRunning ? "Tests en cours…" : "Lancer les tests", "Ctrl+T"u8, false, _tests.Available && !_tests.IsRunning))
                _tests.Start();
            if (ImGui.MenuItem("Ouvrir le projet dans l'éditeur de code"u8))
                _code.OpenProject();
            ImGui.Separator();
            if (ImGui.BeginMenu("Exporter le jeu"u8, !_exporter.IsExporting))
            {
                foreach (var target in Enum.GetValues<ExportTarget>())
                    if (ImGui.MenuItem(Exporter.Label(target)))
                        _exporter.Start(target);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Jouer"u8, "F5"u8, false, _session.PlayState == PlayState.Editing))
                Play();
            if (ImGui.MenuItem("Arrêter"u8, "Maj+F5"u8, false, _session.PlayState != PlayState.Editing))
                _session.Stop();
            ImGui.EndMenu();
        }
        if (_extensions.HasMenus && ImGui.BeginMenu("Outils"u8))
        {
            _extensions.DrawMenu();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Affichage"u8))
        {
            if (ImGui.MenuItem("Cadrer la sélection"u8, "F"u8))
                _viewport.FrameSelection();
            if (ImGui.MenuItem("Disposition par défaut"u8))
                _layoutBuilt = false;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Aide"u8))
        {
            if (ImGui.MenuItem("À propos et crédits"u8))
                _showAbout = true;
            ImGui.EndMenu();
        }

        Toolbar();

        // À droite : état du build et du document.
        var status = _exporter.IsExporting ? "Export…" : _tests.IsRunning ? "Tests…" : _build.IsBuilding ? "Compilation…" : _session.Modules.Module is { } m ? $"{m.Name} · génération {_session.Modules.Generation}" : "aucun module";
        var doc = _session.ScenePath is { } p ? Path.GetFileName(p) + (_session.IsDirty ? " •" : "") : "(scène non enregistrée)";
        var text = $"{doc}    {status}";
        ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(text).X - 16);
        ImGui.TextColored(_build.IsBuilding ? EditorTheme.Warning : EditorTheme.Muted, text);
        ImGui.EndMainMenuBar();
    }

    /// <summary>Jouer, pause, arrêter, vitesse et compilation, au centre de la barre de menus (comme Godot).</summary>
    void Toolbar()
    {
        var editing = _session.PlayState == PlayState.Editing;
        float width = 470;
        ImGui.SameLine((ImGui.GetWindowWidth() - width) * 0.5f);
        ImGui.BeginDisabled(!editing || _session.Modules.Module is null);
        if (ImGui.Button("▶ Jouer"u8))
            Play();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetItemTooltip("Jouer la scène (F5)"u8);
        ImGui.BeginDisabled(editing);
        if (ImGui.Button(_session.PlayState == PlayState.Paused ? "▶ Reprendre" : "❚❚ Pause"))
            _session.TogglePause();
        if (ImGui.Button("■ Arrêter"u8))
            _session.Stop();
        ImGui.EndDisabled();
        ImGui.BeginDisabled(_session.PlayState != PlayState.Paused);
        if (ImGui.Button("▶|"u8))
            _session.StepOnce();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetItemTooltip("Un pas de sim, en pause (F10)"u8);
        ImGui.SetNextItemWidth(90);
        if (ImGui.SliderFloat("##vitesse"u8, ref _speed, 0.125f, 64f, "×%.3g"u8, ImGuiSliderFlags.Logarithmic) && _session.Play is { } p && _session.PlayState == PlayState.Playing)
            p.Clock.TimeScale = _speed;
        ImGui.BeginDisabled(_build.IsBuilding);
        if (ImGui.Button(_build.IsBuilding ? "Compilation…" : "Compiler"))
            BuildAndReload();
        ImGui.EndDisabled();
        if (_session.Play is { } play)
            ImGui.TextColored(EditorTheme.Accent, $"t = {play.World.Tick * play.Clock.StepSeconds:0.0} s");
    }

    void Shortcuts()
    {
        var io = ImGui.GetIO();
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.S, ImGuiInputFlags.RouteGlobal) && _session.ScenePath is not null)
            Save();
        if (!io.WantTextInput)
        {
            if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.Z, ImGuiInputFlags.RouteGlobal))
                _session.History.Undo();
            if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.Y, ImGuiInputFlags.RouteGlobal))
                _session.History.Redo();
        }
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.T, ImGuiInputFlags.RouteGlobal) && _tests.Available && !_tests.IsRunning)
            _tests.Start();
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.B, ImGuiInputFlags.RouteGlobal) && !_build.IsBuilding)
            BuildAndReload();
        if (ImGui.IsKeyPressed(ImGuiKey.F12, false))
        {
            var path = Path.Combine(_project.Root, ".poiesis", "captures", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            _requestCapture(path);
            _log.Info("éditeur", $"Capture : {Path.GetRelativePath(_project.Root, path)}");
        }
        if (_session.Play is { } running && !io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F6, false))
                _session.TogglePause();
            if (ImGui.IsKeyPressed(ImGuiKey.F10))
                _session.StepOnce();
            if (ImGui.IsKeyPressed(ImGuiKey.F9, false))
                _debug.Take(running);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.F5, false))
        {
            if (io.KeyShift)
                _session.Stop();
            else if (_session.PlayState == PlayState.Editing)
                Play();
        }
        _scenePanel.Shortcuts();
    }

    void Play()
    {
        Try(() =>
        {
            _session.StartPlay();
            _session.Play!.Clock.TimeScale = _speed;
            _log.Info("jeu", "Partie lancée.");
        }, "Lancement de la partie");
    }

    void Save() => Try(() =>
    {
        _session.SaveScene();
        _log.Info("éditeur", $"Enregistré : {Path.GetRelativePath(_project.Root, _session.ScenePath!)}");
    }, "Enregistrement");

    void BuildAndReload()
    {
        _reloadAfterBuild = true;
        _build.Start();
    }

    // ----- Disposition, à propos ----------------------------------------------------------------------

    bool _layoutBuilt;

    void BuildDefaultLayout(ImGuiID dockspace)
    {
        _layoutBuilt = true;
        ImGui.Internal.DockBuilderRemoveNode(dockspace);
        ImGui.Internal.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)(1 << 10) | ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.Internal.DockBuilderSetNodeSize(dockspace, ImGui.GetMainViewport().WorkSize);
        var center = dockspace;
        var left = ImGui.Internal.DockBuilderSplitNode(center, ImGuiDir.Left, 0.18f, out _, out center);
        var leftBottom = ImGui.Internal.DockBuilderSplitNode(left, ImGuiDir.Down, 0.45f, out _, out left);
        var right = ImGui.Internal.DockBuilderSplitNode(center, ImGuiDir.Right, 0.28f, out _, out center);
        var bottom = ImGui.Internal.DockBuilderSplitNode(center, ImGuiDir.Down, 0.28f, out _, out center);
        var rightBottom = ImGui.Internal.DockBuilderSplitNode(right, ImGuiDir.Down, 0.5f, out _, out right);
        ImGui.Internal.DockBuilderDockWindow("Scène"u8, left);
        ImGui.Internal.DockBuilderDockWindow("Contenu"u8, leftBottom);
        ImGui.Internal.DockBuilderDockWindow("Viewport"u8, center);
        ImGui.Internal.DockBuilderDockWindow("Inspecteur"u8, right);
        ImGui.Internal.DockBuilderDockWindow("IA"u8, rightBottom);
        ImGui.Internal.DockBuilderDockWindow("Débogage"u8, rightBottom);
        ImGui.Internal.DockBuilderDockWindow("Analyse"u8, rightBottom);
        foreach (var panel in _extensions.Panels)
            ImGui.Internal.DockBuilderDockWindow(panel.Title, rightBottom);
        ImGui.Internal.DockBuilderDockWindow("Console"u8, bottom);
        ImGui.Internal.DockBuilderDockWindow("Entités"u8, bottom);
        ImGui.Internal.DockBuilderDockWindow("Données"u8, bottom);
        ImGui.Internal.DockBuilderFinish(dockspace);
    }

    void About()
    {
        ImGui.SetNextWindowSize(new float2(560, 0), ImGuiCond.Appearing);
        if (ImGui.Begin("À propos"u8, ref _showAbout, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
        {
            _splash.DrawLogo(260);
            ImGui.Spacing();
            ImGui.TextColored(EditorTheme.Accent, "Poiesis Brutal IDE"u8);
            ImGui.TextWrapped("Éditeur de jeux de Poiesis Interactive, construit avec BRUTAL, un framework de développement de jeux d'Ahwoo Limited, utilisé sous la BRUTAL License v1.0."u8);
            ImGui.Spacing();
            ImGui.Text("BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited."u8);
            ImGui.Text("www.ahwoo.com/brutal"u8);
            ImGui.Spacing();
            ImGui.TextDisabled("Dear ImGui, GLFW, Vulkan, VMA, shaderc, FreeType et stb : voir NOTICE.txt et les licences livrées avec BRUTAL."u8);
        }
        ImGui.End();
    }

    // ----- Script de démonstration (--do), pour les captures sans écran ------------------------------

    void RunScript()
    {
        if (_scriptWait > 0)
        {
            _scriptWait--;
            return;
        }
        while (_script.TryDequeue(out var cmd))
        {
            var (verb, arg) = cmd.Split(':', 2) is [var v, var a] ? (v, a) : (cmd, "");
            switch (verb)
            {
                case "select": _session.Selection = _session.Scene.DescendantsAndSelf().FirstOrDefault(n => n.Name == arg); break;
                case "frame": _viewport.FrameSelection(); break;
                case "play": Play(); break;
                case "pause": _session.TogglePause(); break;
                case "step": for (int i = 0; i < int.Parse(arg.Length > 0 ? arg : "1"); i++) _session.StepOnce(); break;
                case "snapshot": if (_session.Play is { } sp) _debug.Take(sp); break;
                case "restore": if (_session.Play is { } rp && rp.Snapshots.Count > 0) rp.Restore(rp.Snapshots[^1]); break;
                case "channel": _debug.EnabledChannels.Add(arg); break;
                case "entity": _session.SelectedEntity = int.TryParse(arg, out var slot) ? slot : FirstLiveEntityOf(arg); break;
                case "advance": _session.Play?.Run((int)(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) / 0.05)); break;
                case "speed": _speed = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
                case "about": _showAbout = true; break;
                case "build": BuildAndReload(); break;
                case "tests": _tests.Start(arg.Length > 0 ? arg : null); break;
                case "series": _analysis.ScriptStart(int.Parse(arg)); break;
                case "ask": _analysis.ScriptAsk(arg); break;
                case "provider": _ai.SelectProvider(arg); break;
                case "open-first-location":
                    if (_log.Snapshot().FirstOrDefault(e => e.Location is not null)?.Location is { } loc)
                        _code.Open(loc);
                    break;
                case "export": _exporter.Start(arg == "windows" ? ExportTarget.WindowsX64 : ExportTarget.LinuxX64); break;
                case "wait": _scriptWait = int.Parse(arg); return;
                case "focus": ImGui.SetWindowFocus(arg); break;
                case "tool":
                    if (!_extensions.RunMenu(arg))
                        _log.Warn("script", $"Commande d'extension inconnue : {arg}");
                    break;
                case "open": OpenScene(Path.Combine(_project.ContentRoot, arg)); break;
                case "save": Save(); break;
                case "copy": _scenePanel.CopySelection(); break;
                case "paste": _scenePanel.PasteHere(); break;
                case "instantiate":
                    try
                    {
                        _session.InstantiateScene(arg, _session.Selection is { HasEditableChildren: true } t ? t : _session.Scene);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("script", ex.Message);
                    }
                    break;
                case "move":
                    var parts = arg.Split('>', 2);
                    _content.Move(parts[0], parts[1]);
                    break;
                case "set":
                    // set:Propriété=valeur (JSON) sur la sélection, comme une modification de l'inspecteur.
                    var kv = arg.Split('=', 2);
                    if (_session.Selection is { } target && target.GetType().GetProperty(kv[0]) is { } prop)
                        _session.History.Execute(new SetPropertyCommand(target, prop,
                            Poiesis.Engine.Scene.ValueCodec.FromJson(System.Text.Json.Nodes.JsonNode.Parse(kv[1])!, prop.PropertyType)));
                    break;
                default: _log.Warn("script", $"Commande inconnue : {cmd}"); break;
            }
        }
    }

    /// <summary>La première entité vivante créée par le nœud nommé <paramref name="nodeName"/> (script de démonstration).</summary>
    int FirstLiveEntityOf(string nodeName)
    {
        if (_session.Play is not { } play)
            return -1;
        foreach (var (entity, owner) in play.Bake.OwnerOf.OrderBy(kv => kv.Key.Index))
            if (play.World.IsAlive(entity) && _session.FindNode(owner)?.Name == nodeName)
                return entity.Index;
        return -1;
    }

    public void Dispose()
    {
        _splash.Dispose();
        _renderer.Dispose();
        _session.Dispose();
    }
}
