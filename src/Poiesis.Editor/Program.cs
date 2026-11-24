using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Poiesis.Editor;
using Poiesis.Editor.Ui;
using Poiesis.Render.Vulkan;

// Poiesis Brutal IDE : point d'entrée.
//
// Usage : Poiesis.Editor <dossier du projet> [options]
//   --scene <fichier>        scène à ouvrir (sinon la première trouvée dans content/scenarios)
//   --capture <fichier.png>  enregistre une capture puis quitte (vérification sans écran, sous Xvfb)
//   --frames <n>             nombre d'images avant la capture (60 par défaut)
//   --size <l>x<h>           taille de la fenêtre (1600x900 par défaut)
//   --splash <secondes>      durée du logo BRUTAL au démarrage (2,5 s par défaut)
//   --do <commande>          script de démonstration : select:<nom>, frame, play, pause, advance:<s>, about, wait:<images>

var options = EditorOptions.Parse(args);

if (!Glfw.Init())
{
    var code = Glfw.GetError(out var message);
    throw new InvalidOperationException($"GLFW : {code}, {message}");
}

try
{
    Run(options);
}
finally
{
    Glfw.Terminate();
}

static void Run(EditorOptions options)
{
    _ = Brutal.VulkanApi.Vulkan.GetInstanceProcAddr;   // charge le chargeur Vulkan de BRUTAL avant GLFW
    Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);

    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
    {
        Title = $"Poiesis Brutal IDE — {Path.GetFileName(options.ProjectRoot)}",
        Size = options.WindowSize,
    });

    using var vk = new VulkanContext(window);
    using var swapchain = new Swapchain(vk, window);
    using var imgui = new ImGuiHost(window, vk, swapchain, options.ProjectRoot);
    using var app = new EditorApp(options, vk, imgui, swapchain.RequestCapture);

    int frameCount = 0;
    while (!window.ShouldClose && !app.QuitRequested)
    {
        Glfw.PollEvents();
        var frame = swapchain.BeginFrame();
        if (frame is null)
        {
            Glfw.WaitEventsTimeout(0.05);
            continue;
        }

        imgui.NewFrame();
        app.Update(frame);
        app.RenderOffscreen(frame);   // viewport 3D, avant la passe de la fenêtre

        swapchain.BeginRenderPass(frame);
        imgui.Render(frame);

        frameCount++;
        if (options.CapturePath is not null && frameCount == options.CaptureFrames)
            swapchain.RequestCapture(options.CapturePath);
        swapchain.EndFrame(frame);

        if (options.CapturePath is not null && frameCount >= options.CaptureFrames)
        {
            Console.WriteLine($"Capture : {Path.GetFullPath(options.CapturePath)}");
            break;
        }
    }
    vk.Device.WaitIdle();
}

/// <summary>Options de la ligne de commande.</summary>
sealed record EditorOptions(string ProjectRoot, string? ScenePath, string? CapturePath, int CaptureFrames, int2 WindowSize, string[] Script, double SplashSeconds)
{
    public static EditorOptions Parse(string[] args)
    {
        string root = Directory.GetCurrentDirectory();
        string? scene = null, capture = null;
        int frames = 60;
        var size = new int2(1600, 900);
        var script = new List<string>();
        double splash = 2.5;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scene": scene = args[++i]; break;
                case "--capture": capture = args[++i]; break;
                case "--frames": frames = int.Parse(args[++i]); break;
                case "--size":
                    var parts = args[++i].Split('x');
                    size = new int2(int.Parse(parts[0]), int.Parse(parts[1]));
                    break;
                case "--do": script.Add(args[++i]); break;
                case "--splash": splash = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                default: root = args[i]; break;
            }
        }
        return new EditorOptions(Path.GetFullPath(root), scene, capture, frames, size, script.ToArray(), splash);
    }
}
