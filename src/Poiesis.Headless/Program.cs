using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

// Usage :
//   Poiesis.Headless <dll du jeu> <dossier content> <scène> [secondes simulées]
//   Poiesis.Headless <dll du jeu> <dossier content> <scène> --runs <n> [--seed <première graine>] [--max <secondes>]
// Le second mode joue une série de parties (une graine par partie) et rend les victoires et les mesures du jeu.
var positional = new List<string>();
int runs = 0, seed = 1;
double max = 1200;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--runs": runs = int.Parse(args[++i]); break;
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--max": max = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: positional.Add(args[i]); break;
    }
}
if (positional.Count < 3)
{
    Console.Error.WriteLine("Usage : Poiesis.Headless <dll du jeu> <dossier content> <scène> [secondes] [--runs n --seed s --max secondes]");
    return 2;
}

var registry = new NodeTypeRegistry();
using var host = new GameModuleHost(registry, Path.GetFullPath(positional[1]));
var module = host.Load(Path.GetFullPath(positional[0]));
var sceneJson = File.ReadAllText(positional[2]);
var serializer = new SceneSerializer(registry);
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (runs > 0)
{
    var series = Series.Run(Path.GetFileName(positional[2]), () => serializer.Read(sceneJson), module, seed, runs, max,
        (i, r) => Console.Error.WriteLine($"partie {i + 1}/{runs} (graine {r.Seed}) : {r.Report.Winner ?? "pas de vainqueur"} en {r.Report.Time:0} s"));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        module = module.Name,
        scene = series.Scene,
        runs = series.Runs.Count,
        winRates = series.WinRates,
        stats = series.Stats.Select(s => new { s.Name, mean = Math.Round(s.Mean, 3), stdDev = Math.Round(s.StdDev, 3), s.Min, s.Max }),
        summary = series.Summary(),
    }, json));
    return 0;
}

var seconds = positional.Count > 3 ? double.Parse(positional[3], System.Globalization.CultureInfo.InvariantCulture) : 120;
var session = new PlaySession(serializer.Read(sceneJson), module);
int steps = (int)Math.Round(seconds / session.Clock.StepSeconds);
var sw = Stopwatch.StartNew();
session.Run(steps);
sw.Stop();

// Lignes de HUD et bilan fournis par le jeu, s'il en dessine ou en produit.
var hud = new List<string>();
if (module is IWorldVisualizer visualizer)
{
    var draw = new Poiesis.Engine.Debug.DrawContext();
    visualizer.Draw(session.World, draw);
    hud.AddRange(draw.Hud.Select(h => h.Text));
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    module = module.Name,
    scene = Path.GetFileName(positional[2]),
    simulatedSeconds = seconds,
    steps,
    msPerStep = sw.Elapsed.TotalMilliseconds / Math.Max(steps, 1),
    entities = session.World.EntityCount,
    systems = session.Schedule.Timings.Select(t => new { name = t.Name, averageMs = Math.Round(t.AverageMs, 3), maxMs = Math.Round(t.MaxMs, 3) }),
    report = (module as IGameReporter)?.Report(session.World),
    hud,
}, json));
return 0;
