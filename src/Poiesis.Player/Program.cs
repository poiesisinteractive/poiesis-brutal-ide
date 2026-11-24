using System.Text.Json.Nodes;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Poiesis.Audio;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;
using Poiesis.Player;
using Poiesis.Render.Vulkan;
using Poiesis.Runtime;

// Poiesis Player : joue un jeu exporté. Menu principal (choix du scénario), partie avec le HUD du jeu, pause, options,
// écran de fin, crédits BRUTAL, son.
//
// Usage : <jeu> [game.json] [--start] [--screen menu|play|pause|options|end] [--advance <s>] [--select <slot>]
//               [--capture <png> --frames <n>] [--splash <s>] [--credits]
// Sans argument, lit game.json à côté de l'exécutable et ouvre le menu principal.

string manifestPath = Path.Combine(AppContext.BaseDirectory, "game.json");
string? capture = null, startScreen = null;
int frames = 60, selectAtStart = -1;
double splash = 2.5, advance = 0;
bool showCredits = false;
List<string> soundCredits = [];
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--capture": capture = args[++i]; break;
        case "--frames": frames = int.Parse(args[++i]); break;
        case "--splash": splash = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--credits": showCredits = true; break;
        case "--advance": advance = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--start": startScreen = "play"; break;
        case "--screen": startScreen = args[++i]; break;
        case "--select": selectAtStart = int.Parse(args[++i]); break;
        default: manifestPath = Path.GetFullPath(args[i]); break;
    }
}

var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
var manifest = GameManifest.Load(manifestPath);
var contentRoot = Path.Combine(root, manifest.Content);
var optionsPath = Path.Combine(root, "user", "options.json");
var options = PlayerOptions.Load(optionsPath);

if (!Glfw.Init())
{
    var code = Glfw.GetError(out var message);
    throw new InvalidOperationException($"GLFW : {code}, {message}");
}

try
{
    Run();
}
finally
{
    Glfw.Terminate();
}

void Run()
{
    _ = Vulkan.GetInstanceProcAddr;   // charge le chargeur Vulkan de BRUTAL avant GLFW
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = manifest.Name, Size = new int2(1600, 900) });
    using var vk = new VulkanContext(window);
    using var swapchain = new Swapchain(vk, window);
    using var imgui = new ImGuiHost(window, vk, swapchain, Path.Combine(root, "user"));
    using var renderer = new SceneRenderer(vk, Swapchain.FramesInFlight);
    using var logo = new Splash(vk, imgui.Renderer, splash);
    using var audio = new AudioEngine(contentRoot, m => Console.Error.WriteLine($"son : {m}"));
    soundCredits = AudioEngine.SoundCredits(contentRoot);
    var textures = new ViewportTextures(imgui.Renderer, renderer);
    var view = new WorldView(renderer);

    // Le jeu : sa DLL et ses scénarios.
    var registry = new NodeTypeRegistry();
    using var host = new GameModuleHost(registry, contentRoot);
    var module = host.Load(Path.Combine(root, manifest.Assembly));
    var serializer = new SceneSerializer(registry, contentRoot);
    var scenarios = Scenarios(manifest.Scene);
    int scenario = Math.Max(0, scenarios.FindIndex(s => s.Path == Path.GetFullPath(Path.Combine(root, manifest.Scene))));
    var visualizer = module as IWorldVisualizer;
    var hud = module as IGameHud;
    var reporter = module as IGameReporter;

    PlaySession session = null!;
    long soundCursor = 0;
    int selected = -1;
    bool endShown = false;
    var screen = Screen.Menu;
    var beforeOptions = Screen.Menu;
    bool credits = showCredits;
    float speed = 1;

    void NewSession(bool play)
    {
        var scene = serializer.Read(File.ReadAllText(scenarios[scenario].Path), scenarios[scenario].Path);
        session = new PlaySession(scene, module);
        session.Clock.TimeScale = play ? speed : 0;
        view.Yaw = 0.6f;
        view.Pitch = 0.55f;
        view.FrameAll(session.World, maxRadius: 10_000);   // l'action, pas les astres
        soundCursor = 0;
        selected = -1;
        endShown = false;
        audio.StopAll();
    }

    void Play()
    {
        NewSession(play: true);
        screen = Screen.Playing;
    }

    NewSession(play: false);
    switch (startScreen)
    {
        case "play" or "pause" or "end":
            Play();
            break;
        case "options":
            screen = Screen.Options;
            break;
    }
    if (advance > 0)
        session.Run((int)Math.Round(advance / session.Clock.StepSeconds));
    if (startScreen == "pause")
    {
        session.Clock.TimeScale = 0;
        screen = Screen.Paused;
    }
    if (selectAtStart >= 0)
        selected = selectAtStart;

    int frameCount = 0;
    while (!window.ShouldClose)
    {
        Glfw.PollEvents();
        var frame = swapchain.BeginFrame();
        if (frame is null)
        {
            Glfw.WaitEventsTimeout(0.05);
            continue;
        }

        imgui.NewFrame();
        bool showingSplash = logo.Draw();
        if (!showingSplash)
        {
            var io = ImGui.GetIO();
            audio.MasterVolume = options.MasterVolume;
            audio.EffectsVolume = options.EffectsVolume;
            session.Update(io.DeltaTime);
            if (screen == Screen.Playing && session.Clock.TimeScale > 0)
                audio.Update(session.World, ref soundCursor, view.Listener());
            else
                soundCursor = session.World.TryResource<Poiesis.Engine.Audio.SoundEvents>(out var se) ? se.Next : 0;
            if (screen == Screen.Menu)
                view.Yaw += io.DeltaTime * 0.04f;   // le scénario tourne lentement derrière le menu

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.Pos);
            ImGui.SetNextWindowSize(vp.Size);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 0));
            ImGui.Begin("##jeu"u8, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDocking);
            ImGui.PopStyleVar();
            var size = new int2(Math.Max((int)vp.Size.X, 1), Math.Max((int)vp.Size.Y, 1));
            renderer.Resize(size);
            view.ImageMin = ImGui.GetCursorScreenPos();
            view.ImageSize = new float2(size.X, size.Y);
            ImGui.Image(textures.Viewport(frame.Slot), view.ImageSize);
            bool hovered = ImGui.IsItemHovered();
            var dl = ImGui.GetWindowDrawList();

            if (screen == Screen.Playing)
            {
                PlayingInput(io, hovered);
                // Fin de bataille : le bilan, une fois, quelques secondes après la victoire.
                if (!endShown && reporter?.Report(session.World) is { Finished: true } report && session.Time > report.Time + 3)
                {
                    endShown = true;
                    session.Clock.TimeScale = 0;
                    screen = Screen.End;
                }
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                screen = screen switch
                {
                    Screen.Paused => Resume(),
                    Screen.Options => beforeOptions,
                    _ => screen,
                };
            }

            // HUD du jeu (ou ses lignes de texte s'il n'en a pas), en partie.
            if (screen is Screen.Playing or Screen.Paused or Screen.End)
            {
                if (hud is not null)
                {
                    var ctx = new HudContext
                    {
                        Min = view.ImageMin,
                        Size = view.ImageSize,
                        Project = p => view.Project(p, out var s) ? view.ImageMin + s : null,
                        SelectedEntity = selected,
                        Time = session.Time,
                        TimeScale = (float)session.Clock.TimeScale,
                    };
                    try
                    {
                        hud.DrawHud(session.World, ctx);
                    }
                    catch (Exception ex)
                    {
                        dl.AddText(view.ImageMin + new float2(14, 80), EditorTheme.Col(EditorTheme.Error), $"HUD : {ex.Message}");
                    }
                    selected = ctx.SelectedEntity;
                }
                else
                {
                    view.DrawOverlay(dl, view.ImageMin + new float2(14, 34));
                }
                if (options.ShowHelp && screen == Screen.Playing)
                    dl.AddText(view.ImageMin + new float2(14, view.ImageSize.Y - 28), EditorTheme.Col(EditorTheme.Muted),
                        "Glisser : tourner · clic droit : déplacer · molette : zoom · clic : fiche d'un vaisseau · Espace : pause · +/− : vitesse · Origine : tout voir · F1 : crédits · Échap : menu"u8);
            }
            ImGui.End();

            switch (screen)
            {
                case Screen.Menu: MainMenu(); break;
                case Screen.Paused: PauseMenu(); break;
                case Screen.Options: OptionsMenu(); break;
                case Screen.End: EndScreen(); break;
            }
            if (credits)
                Credits(logo, ref credits);
        }

        if (!showingSplash)
        {
            view.Begin();
            view.Frame.Post = view.Frame.Post with { Bloom = options.Bloom };
            view.DrawEntities(session.World);
            view.DrawGame(session.World, visualizer, null, screen == Screen.Menu ? -1 : selected);
            renderer.Render(frame.Commands, frame.Slot, view.Frame);
        }

        swapchain.BeginRenderPass(frame);
        imgui.Render(frame);
        frameCount++;
        if (capture is not null && frameCount == frames)
            swapchain.RequestCapture(capture);
        swapchain.EndFrame(frame);
        if (capture is not null && frameCount >= frames)
        {
            Console.WriteLine($"Capture : {Path.GetFullPath(capture)}");
            break;
        }
    }
    vk.Device.WaitIdle();

    Screen Resume()
    {
        session.Clock.TimeScale = speed;
        return Screen.Playing;
    }

    void PlayingInput(ImGuiIOPtr io, bool hovered)
    {
        if (hovered)
        {
            if (io.MouseWheel != 0)
                view.Zoom(io.MouseWheel);
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
                view.Orbit(io.MouseDelta);
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f))
                view.Pan(io.MouseDelta);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !ImGui.Internal.IsMouseDragPastThreshold(ImGuiMouseButton.Left, 3f))
            {
                var hit = view.Pick(session.World, io.MousePos - view.ImageMin);
                selected = hit.IsNull ? -1 : hit.Index;
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
            session.Clock.TimeScale = session.Clock.TimeScale == 0 ? speed : 0;
        if (ImGui.IsKeyPressed(ImGuiKey.KeypadAdd) || ImGui.IsKeyPressed(ImGuiKey.Equal))
            session.Clock.TimeScale = speed = MathF.Min(speed * 2, 64);
        if (ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract) || ImGui.IsKeyPressed(ImGuiKey.Minus))
            session.Clock.TimeScale = speed = MathF.Max(speed / 2, 0.125f);
        if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
            view.FrameAll(session.World, maxRadius: 10_000);
        if (ImGui.IsKeyPressed(ImGuiKey.F1, false))
            credits = !credits;
        if (ImGui.IsKeyPressed(ImGuiKey.H, false))
            options = options with { ShowHelp = !options.ShowHelp };
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            session.Clock.TimeScale = 0;
            screen = Screen.Paused;
        }
    }

    // ----- Écrans -----------------------------------------------------------------------------------

    bool BeginMenu(string id, float2 size, float2 pivot, float2? at = null)
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(at ?? vp.Pos + vp.Size * 0.5f, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowBgAlpha(0.88f);
        return ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking);
    }

    bool Button(string label) => ImGui.Button(label, new float2(-1, 38));

    void Title(string text, float size)
    {
        ImGui.PushFont(ImGui.GetFont(), size);
        ImGui.TextColored(EditorTheme.Accent, text);
        ImGui.PopFont();
    }

    void MainMenu()
    {
        var vp = ImGui.GetMainViewport();
        if (BeginMenu("##menu", new float2(420, 0), new float2(0, 0.5f), vp.Pos + new float2(80, vp.Size.Y * 0.5f)))
        {
            Title(manifest.Name.ToUpperInvariant(), 40);
            ImGui.TextDisabled("Choisissez une bataille"u8);
            ImGui.Spacing();
            for (int i = 0; i < scenarios.Count; i++)
                if (ImGui.Selectable(scenarios[i].Title, i == scenario, ImGuiSelectableFlags.None, new float2(0, 26)) && i != scenario)
                {
                    scenario = i;
                    NewSession(play: false);
                }
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (Button("Jouer"))
                Play();
            if (Button("Options"))
            {
                beforeOptions = Screen.Menu;
                screen = Screen.Options;
            }
            if (Button("Crédits"))
                credits = !credits;
            if (Button("Quitter"))
                window.ShouldClose = true;
        }
        ImGui.End();
    }

    void PauseMenu()
    {
        if (BeginMenu("##pause", new float2(360, 0), new float2(0.5f, 0.5f)))
        {
            Title("PAUSE", 30);
            ImGui.TextDisabled(scenarios[scenario].Title);
            ImGui.Spacing();
            if (Button("Reprendre"))
                screen = Resume();
            if (Button("Recommencer"))
                Play();
            if (Button("Options"))
            {
                beforeOptions = Screen.Paused;
                screen = Screen.Options;
            }
            if (Button("Menu principal"))
            {
                NewSession(play: false);
                screen = Screen.Menu;
            }
            if (Button("Quitter le jeu"))
                window.ShouldClose = true;
        }
        ImGui.End();
    }

    void OptionsMenu()
    {
        if (BeginMenu("##options", new float2(440, 0), new float2(0.5f, 0.5f)))
        {
            Title("OPTIONS", 30);
            ImGui.Spacing();
            float master = options.MasterVolume, effects = options.EffectsVolume, bloom = options.Bloom;
            bool help = options.ShowHelp;
            ImGui.SliderFloat("Volume général"u8, ref master, 0, 1, "%.2f"u8);
            ImGui.SliderFloat("Effets sonores"u8, ref effects, 0, 1, "%.2f"u8);
            ImGui.SliderFloat("Halo (bloom)"u8, ref bloom, 0, 1.5f, "%.2f"u8);
            ImGui.Checkbox("Aide en bas de l'écran"u8, ref help);
            var updated = options with { MasterVolume = master, EffectsVolume = effects, Bloom = bloom, ShowHelp = help };
            if (updated != options)
                options = updated;
            ImGui.TextDisabled(audio.Available ? $"Son : {audio.Driver}" : "Son indisponible sur cette machine.");
            ImGui.Spacing();
            if (Button("Retour"))
            {
                options.Save(optionsPath);
                screen = beforeOptions;
            }
        }
        ImGui.End();
    }

    void EndScreen()
    {
        var report = reporter?.Report(session.World);
        var vp = ImGui.GetMainViewport();
        // Sous la bannière de victoire du HUD (vers 22 % de la hauteur).
        if (BeginMenu("##fin", new float2(560, 0), new float2(0.5f, 0), vp.Pos + new float2(vp.Size.X * 0.5f, vp.Size.Y * 0.36f)))
        {
            Title(report?.Winner is { } w ? $"Victoire : {w}" : "Fin de la bataille", 32);
            ImGui.TextDisabled($"{scenarios[scenario].Title} · {TimeSpan.FromSeconds(report?.Time ?? session.Time):mm\\:ss}");
            ImGui.Spacing();
            if (report is not null && ImGui.BeginTable("bilan"u8, 2, ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("mesure"u8, ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("valeur"u8, ImGuiTableColumnFlags.WidthFixed, 90);
                foreach (var (name, value) in report.Metrics)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(name);
                    ImGui.TableNextColumn();
                    ImGui.Text(value % 1 == 0 ? $"{value:0}" : $"{value:0.0}");
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            if (Button("Rejouer"))
                Play();
            if (Button("Continuer à regarder"))
                screen = Resume();
            if (Button("Menu principal"))
            {
                NewSession(play: false);
                screen = Screen.Menu;
            }
        }
        ImGui.End();
    }
}

// Les scénarios du jeu : les scènes de content/scenarios, nommées par leur racine ; celle du manifeste d'abord.
List<(string Path, string Title)> Scenarios(string manifestScene)
{
    var dir = Path.Combine(contentRoot, "scenarios");
    var list = new List<(string, string)>();
    var files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.scene.json").Order(StringComparer.Ordinal).ToList() : [];
    var start = Path.GetFullPath(Path.Combine(root, manifestScene));
    if (!files.Contains(start))
        files.Insert(0, start);
    foreach (var f in files)
    {
        string title = Path.GetFileName(f).Replace(".scene.json", "");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(f))?["name"]?.GetValue<string>() is { Length: > 0 } name)
                title = name;
        }
        catch (Exception)
        {
        }
        list.Add((Path.GetFullPath(f), title));
    }
    return list;
}

// Écran de crédits : obligatoire pour tout produit construit avec BRUTAL (licence, section 2.1).
void Credits(Splash logo, ref bool open)
{
    var vp = ImGui.GetMainViewport();
    ImGui.SetNextWindowPos(vp.Pos + vp.Size * 0.5f, ImGuiCond.Always, new float2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new float2(620, 0));
    ImGui.Begin("Crédits"u8, ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking);
    ImGui.TextColored(EditorTheme.Accent, manifest.Name);
    foreach (var line in manifest.Credits)
        ImGui.TextWrapped(line);
    ImGui.Separator();
    ImGui.Spacing();
    logo.DrawLogo(260);
    ImGui.Spacing();
    ImGui.TextWrapped("Ce jeu est construit avec BRUTAL, un framework de développement de jeux d'Ahwoo Limited, utilisé sous la BRUTAL License v1.0."u8);
    ImGui.Text("BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited."u8);
    ImGui.Text("www.ahwoo.com/brutal"u8);
    ImGui.Spacing();
    if (soundCredits.Count > 0)
    {
        ImGui.SeparatorText("Sons"u8);
        if (ImGui.BeginChild("sons"u8, new float2(0, 140)))
            foreach (var line in soundCredits)
                ImGui.TextWrapped(line);
        ImGui.EndChild();
    }
    ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Muted);
    ImGui.TextWrapped("Moteur Poiesis, son SDL3_mixer. Licences des composants tiers : NOTICE.txt et dossier LICENSES. F1 pour fermer."u8);
    ImGui.PopStyleColor();
    ImGui.End();
}

enum Screen { Menu, Playing, Paused, Options, End }
