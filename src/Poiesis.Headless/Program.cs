using System.Diagnostics;
using System.Text.Json;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

// Usage : Poiesis.Headless <dll du jeu> <dossier content> <scène> [secondes simulées]
if (args.Length < 3)
{
    Console.Error.WriteLine("Usage : Poiesis.Headless <dll du jeu> <dossier content> <scène> [secondes]");
    return 2;
}

var registry = new NodeTypeRegistry();
using var host = new GameModuleHost(registry, Path.GetFullPath(args[1]));
var module = host.Load(Path.GetFullPath(args[0]));
var scene = new SceneSerializer(registry).Read(File.ReadAllText(args[2]));
var seconds = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 120;

var session = new PlaySession(scene, module);
int steps = (int)Math.Round(seconds / session.Clock.StepSeconds);
var sw = Stopwatch.StartNew();
session.Run(steps);
sw.Stop();

var report = new
{
    module = module.Name,
    scene = Path.GetFileName(args[2]),
    simulatedSeconds = seconds,
    steps,
    msPerStep = sw.Elapsed.TotalMilliseconds / Math.Max(steps, 1),
    entities = session.World.EntityCount,
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;
