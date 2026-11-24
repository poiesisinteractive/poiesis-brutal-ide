using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;
using Poiesis.Player;
using Poiesis.Render.Vulkan;
using Poiesis.Runtime;

// Poiesis Player : joue un jeu exporté.
//
// Usage : <jeu> [game.json] [--capture <png> --frames <n>] [--splash <s>] [--advance <s>]
// Sans argument, lit game.json à côté de l'exécutable.

string manifestPath = Path.Combine(AppContext.BaseDirectory, "game.json");
string? capture = null;
int frames = 60;
double splash = 2.5, advance = 0;
bool showCredits = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--capture": capture = args[++i]; break;
        case "--frames": frames = int.Parse(args[++i]); break;
        case "--splash": splash = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--credits": showCredits = true; break;
        case "--advance": advance = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: manifestPath = Path.GetFullPath(args[i]); break;
    }
}

var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
var manifest = GameManifest.Load(manifestPath);

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
    var textures = new ViewportTextures(imgui.Renderer, renderer);
    var view = new WorldView(renderer);

    // Le jeu : sa DLL, sa scène de départ, une partie.
    var registry = new NodeTypeRegistry();
    using var host = new GameModuleHost(registry, Path.Combine(root, manifest.Content));
    var module = host.Load(Path.Combine(root, manifest.Assembly));
    var scene = new SceneSerializer(registry, Path.Combine(root, manifest.Content)).Read(File.ReadAllText(Path.Combine(root, manifest.Scene)));
    var session = new PlaySession(scene, module);
    if (advance > 0)
        session.Run((int)Math.Round(advance / session.Clock.StepSeconds));
    var visualizer = module as IWorldVisualizer;
    bool framed = false, credits = showCredits, help = true;
    float speed = 1;

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
            session.Update(io.DeltaTime);

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
            if (!framed)
            {
                view.FrameAll(session.World, maxRadius: 10_000);   // l'action, pas les astres
                framed = true;
            }
            ImGui.Image(textures.Viewport(frame.Slot), view.ImageSize);

            // Commandes : caméra à la souris, pause, vitesse, crédits.
            if (ImGui.IsItemHovered())
            {
                if (io.MouseWheel != 0)
                    view.Zoom(io.MouseWheel);
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
                    view.Orbit(io.MouseDelta);
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f))
                    view.Pan(io.MouseDelta);
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
                help = !help;
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                window.ShouldClose = true;

            var dl = ImGui.GetWindowDrawList();
            var at = view.ImageMin + new float2(14, 12);
            string state = session.Clock.TimeScale == 0 ? "PAUSE" : $"×{session.Clock.TimeScale:0.###}";
            dl.AddText(at, EditorTheme.Col(EditorTheme.Accent), $"{manifest.Name}   t = {session.Time:0} s   {state}");
            view.DrawOverlay(dl, at + new float2(0, 22));
            if (help)
                dl.AddText(view.ImageMin + new float2(14, view.ImageSize.Y - 28), EditorTheme.Col(EditorTheme.Muted),
                    "Glisser : tourner · clic droit : déplacer · molette : zoom · Espace : pause · +/− : vitesse · Origine : tout voir · F1 : crédits · H : aide · Échap : quitter"u8);
            ImGui.End();

            if (credits)
                Credits(logo);
        }

        if (!showingSplash)
        {
            view.Begin();
            view.DrawEntities(session.World);
            view.DrawGame(session.World, visualizer, null, -1);
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
}

// Écran de crédits : obligatoire pour tout produit construit avec BRUTAL (licence, section 2.1).
void Credits(Splash logo)
{
    var vp = ImGui.GetMainViewport();
    ImGui.SetNextWindowPos(vp.Pos + vp.Size * 0.5f, ImGuiCond.Always, new float2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new float2(620, 0));
    ImGui.Begin("Crédits"u8, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking);
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
    ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Muted);
    ImGui.TextWrapped("Moteur Poiesis. Licences des composants tiers : NOTICE.txt et dossier LICENSES. F1 pour fermer."u8);
    ImGui.PopStyleColor();
    ImGui.End();
}
