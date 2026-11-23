using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Poiesis.Editor;

// Poiesis Brutal IDE : point d'entrée.
//
// Usage : Poiesis.Editor <dossier du projet> [<dll du jeu>] [<scène>]
//
// Jalon 1, en cours : la fenêtre et la boucle sont en place. Restent à brancher, dans cet ordre
// (voir docs/ROADMAP.md) : le device Vulkan et la swapchain, ImGui sur ses backends GLFW et Vulkan avec le docking,
// puis les panneaux (Scène, Inspecteur, Viewport, Console, IA) qui lisent et modifient l'EditorSession.

var projectRoot = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
using var session = new EditorSession(projectRoot);
session.Log += Console.WriteLine;

if (args.Length > 1)
    session.LoadGameModule(args[1]);
if (args.Length > 2)
    session.OpenScene(args[2]);

if (!Glfw.Init())
{
    var code = Glfw.GetError(out var message);
    throw new InvalidOperationException($"GLFW : {code}, {message}");
}

Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);   // pas de contexte OpenGL : le rendu est en Vulkan
var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
{
    Title = $"Poiesis Brutal IDE — {Path.GetFileName(projectRoot)}",
    Size = new int2(1600, 900),
});

var clock = Stopwatch.StartNew();
var last = clock.Elapsed.TotalSeconds;
while (!window.ShouldClose)
{
    Glfw.PollEvents();
    var now = clock.Elapsed.TotalSeconds;
    session.Update(now - last);
    last = now;
    // TODO(J1) : construire l'interface ImGui, rendre le viewport, présenter la swapchain.
}

Glfw.Terminate();
