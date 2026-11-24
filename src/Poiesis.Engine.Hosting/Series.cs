using System.Diagnostics;
using System.Reflection;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Hosting;

public sealed record SeriesRun(int Seed, GameReport Report, double MsPerStep);

/// <summary>Une statistique sur la série : moyenne, écart type, extrêmes.</summary>
public sealed record SeriesStat(string Name, double Mean, double StdDev, double Min, double Max);

public sealed record SeriesResult(string Scene, IReadOnlyList<SeriesRun> Runs)
{
    /// <summary>Part des victoires par vainqueur (« aucun » pour les parties non finies).</summary>
    public IReadOnlyDictionary<string, double> WinRates =>
        Runs.GroupBy(r => r.Report.Winner ?? "aucun").ToDictionary(g => g.Key, g => g.Count() / (double)Runs.Count);

    public IReadOnlyList<SeriesStat> Stats
    {
        get
        {
            var names = new List<string> { "durée (s)", "ms par pas" };
            names.AddRange(Runs.SelectMany(r => r.Report.Metrics.Keys).Distinct());
            return names.Select(n =>
            {
                var values = Runs.Select(r => n switch
                {
                    "durée (s)" => r.Report.Time,
                    "ms par pas" => r.MsPerStep,
                    _ => r.Report.Metrics.TryGetValue(n, out var v) ? v : double.NaN,
                }).Where(v => !double.IsNaN(v)).ToArray();
                if (values.Length == 0)
                    return new SeriesStat(n, 0, 0, 0, 0);
                double mean = values.Average();
                double sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Length - 1));
                return new SeriesStat(n, mean, sd, values.Min(), values.Max());
            }).ToList();
        }
    }

    /// <summary>Résumé lisible (et lisible par l'IA) de la série.</summary>
    public string Summary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Série de {Runs.Count} parties sur « {Scene} » (graines {Runs.Min(r => r.Seed)} à {Runs.Max(r => r.Seed)}).");
        sb.AppendLine("Victoires : " + string.Join(", ", WinRates.OrderByDescending(w => w.Value).Select(w => $"{w.Key} {w.Value:P0}")));
        foreach (var s in Stats)
            sb.AppendLine($"- {s.Name} : moyenne {s.Mean:0.##} (écart type {s.StdDev:0.##}, de {s.Min:0.##} à {s.Max:0.##})");
        return sb.ToString();
    }
}

/// <summary>
/// Joue une scène plusieurs fois, avec des graines différentes, jusqu'à la fin de chaque partie (ou une durée maximale),
/// et rassemble les bilans du jeu (<see cref="IGameReporter"/>). C'est la base de l'équilibrage.
/// </summary>
public static class Series
{
    /// <summary>
    /// Remplace la graine de la scène : toute propriété entière nommée <c>Seed</c> d'un nœud. Rend vrai si une graine a été trouvée.
    /// </summary>
    public static bool SetSeed(Node scene, int seed)
    {
        bool found = false;
        foreach (var node in scene.DescendantsAndSelf())
        {
            var p = node.GetType().GetProperty("Seed", BindingFlags.Public | BindingFlags.Instance);
            if (p is not null && p.PropertyType == typeof(int) && p.CanWrite)
            {
                p.SetValue(node, seed);
                found = true;
            }
        }
        return found;
    }

    public static SeriesResult Run(string sceneName, Func<Node> loadScene, IGameModule module, int firstSeed, int runs, double maxSeconds,
        Action<int, SeriesRun>? progress = null, CancellationToken ct = default)
    {
        if (module is not IGameReporter reporter)
            throw new InvalidOperationException($"Le module {module.Name} ne fournit pas de bilan de partie (IGameReporter).");
        var results = new List<SeriesRun>();
        for (int i = 0; i < runs; i++)
        {
            ct.ThrowIfCancellationRequested();
            int seed = firstSeed + i;
            var scene = loadScene();
            SetSeed(scene, seed);
            var session = new PlaySession(scene, module);
            int maxSteps = (int)Math.Round(maxSeconds / session.Clock.StepSeconds);
            var sw = Stopwatch.StartNew();
            int steps = 0;
            GameReport report;
            while (true)
            {
                int chunk = Math.Min(200, maxSteps - steps);
                session.Run(chunk);
                steps += chunk;
                report = reporter.Report(session.World);
                if (report.Finished || steps >= maxSteps || ct.IsCancellationRequested)
                    break;
            }
            var run = new SeriesRun(seed, report, sw.Elapsed.TotalMilliseconds / Math.Max(steps, 1));
            results.Add(run);
            progress?.Invoke(i, run);
        }
        return new SeriesResult(sceneName, results);
    }
}
