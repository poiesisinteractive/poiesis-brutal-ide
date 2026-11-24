using System.Text.RegularExpressions;
using Poiesis.AI.Agent;

namespace Poiesis.Editor;

public sealed record TestFailure(string Name, string Message, SourceLocation? Location);

public sealed record TestRun(bool Succeeded, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures, string Output, TimeSpan Duration);

/// <summary>
/// Lance les tests du jeu (<c>dotnet test</c>) en arrière-plan. Chaque test en échec arrive dans la console avec son
/// message et, quand la pile d'appels le permet, l'emplacement de l'échec dans le code du jeu (cliquable).
/// </summary>
public sealed partial class TestService(ProjectConfig project, LogBuffer log)
{
    Task<TestRun>? _running;

    public bool IsRunning => _running is { IsCompleted: false };

    public TestRun? Last { get; private set; }

    public bool Available => project.TestProject is not null;

    public void Start(string? filter = null)
    {
        if (IsRunning || project.TestProject is null)
            return;
        log.Info("tests", $"Tests de {project.TestProject}…");
        _running = Task.Run(() => RunAsync(project.Abs(project.TestProject), filter));
    }

    public TestRun? TakeCompleted()
    {
        if (_running is not { IsCompleted: true } task)
            return null;
        _running = null;
        Last = task.IsCompletedSuccessfully ? task.Result : new TestRun(false, 0, 0, 0, [], task.Exception?.InnerException?.Message ?? "échec", TimeSpan.Zero);
        foreach (var f in Last.Failures)
            log.Add(LogLevel.Error, "tests", $"✗ {f.Name} : {f.Message}", f.Location);
        var summary = $"{Last.Passed} réussis, {Last.Failed} en échec, {Last.Skipped} ignorés ({Last.Duration.TotalSeconds:0} s)";
        if (Last.Succeeded)
            log.Info("tests", $"Tests réussis : {summary}");
        else if (Last.Passed + Last.Failed == 0)
        {
            // Le plus souvent, le jeu ou ses tests ne compilent pas : on montre les erreurs, cliquables.
            var diagnostics = BuildService.Parse(Last.Output).Where(d => d.IsError).ToList();
            foreach (var d in diagnostics)
                log.Add(LogLevel.Error, "tests", $"{Path.GetFileName(d.File)}({d.Line},{d.Column}) {d.Code} : {d.Message}", new SourceLocation(d.File, d.Line, d.Column));
            log.Error("tests", diagnostics.Count > 0
                ? "Les tests n'ont pas pu tourner : le code ne compile pas."
                : "Les tests n'ont pas pu tourner :\n" + ProcessRunner.Tail(Last.Output, 2000));
        }
        else
            log.Error("tests", $"Tests en échec : {summary}");
        return Last;
    }

    async Task<TestRun> RunAsync(string csproj, string? filter)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var args = new List<string> { "test", csproj, "-nologo", "--logger", "console;verbosity=normal" };
        if (filter is not null)
            args.AddRange(["--filter", filter]);
        var r = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(csproj)!, TimeSpan.FromMinutes(15), default);
        return Parse(r.Succeeded, r.Output, sw.Elapsed, project.Root);
    }

    /// <summary>Lit la sortie de <c>dotnet test</c> : totaux, et pour chaque échec son message et son emplacement.</summary>
    public static TestRun Parse(bool succeeded, string output, TimeSpan duration, string projectRoot)
    {
        int passed = 0, failed = 0, skipped = 0;
        foreach (Match m in SummaryRegex().Matches(output))
        {
            failed += int.Parse(m.Groups["failed"].Value);
            passed += int.Parse(m.Groups["passed"].Value);
            skipped += int.Parse(m.Groups["skipped"].Value);
        }

        // Format détaillé (verbosité « normal ») : « Total tests: 3 », puis « Passed: 2 » et « Failed: 1 » sur leurs lignes.
        if (passed + failed + skipped == 0)
        {
            foreach (Match m in DetailedCountRegex().Matches(output))
            {
                int n = int.Parse(m.Groups["n"].Value);
                switch (m.Groups["kind"].Value)
                {
                    case "Passed": passed += n; break;
                    case "Failed": failed += n; break;
                    case "Skipped": skipped += n; break;
                }
            }
        }

        var failures = new List<TestFailure>();
        var lines = output.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var head = FailedRegex().Match(lines[i]);
            if (!head.Success)
                continue;
            string name = head.Groups["name"].Value.Trim();
            string message = "";
            SourceLocation? location = null;
            for (int j = i + 1; j < lines.Length && !FailedRegex().IsMatch(lines[j]) && !lines[j].Contains("Passed!") && !lines[j].Contains("Failed!"); j++)
            {
                if (lines[j].Trim() == "Error Message:" && j + 1 < lines.Length)
                    message = lines[j + 1].Trim();
                var at = StackRegex().Match(lines[j]);
                // Le premier cadre de la pile qui tombe dans le projet du jeu.
                if (location is null && at.Success && Path.GetFullPath(at.Groups["file"].Value).StartsWith(projectRoot, StringComparison.Ordinal))
                    location = new SourceLocation(at.Groups["file"].Value, int.Parse(at.Groups["line"].Value));
            }
            failures.Add(new TestFailure(name, message, location));
        }
        return new TestRun(succeeded && failed == 0, passed, failed, skipped, failures, output, duration);
    }

    [GeneratedRegex(@"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+)")]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"^\s*(?<kind>Passed|Failed|Skipped):\s*(?<n>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DetailedCountRegex();

    [GeneratedRegex(@"^\s*Failed (?<name>[^\[\r\n]+)\s*\[")]
    private static partial Regex FailedRegex();

    [GeneratedRegex(@"\sin (?<file>.+?):line (?<line>\d+)")]
    private static partial Regex StackRegex();
}
