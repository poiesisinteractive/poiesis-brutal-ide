using System.Text.RegularExpressions;
using Poiesis.AI.Agent;

namespace Poiesis.Editor;

/// <summary>Une erreur ou un avertissement de compilation, cliquable dans la console.</summary>
public sealed record BuildDiagnostic(string File, int Line, int Column, bool IsError, string Code, string Message);

public sealed record BuildResult(bool Succeeded, IReadOnlyList<BuildDiagnostic> Diagnostics, string Output, TimeSpan Duration);

/// <summary>Compile la DLL du jeu en arrière-plan avec <c>dotnet build</c> et analyse les diagnostics.</summary>
public sealed partial class BuildService(ProjectConfig project, LogBuffer log)
{
    Task<BuildResult>? _running;

    public bool IsBuilding => _running is { IsCompleted: false };

    public BuildResult? Last { get; private set; }

    /// <summary>Lance un build (sans effet s'il y en a déjà un). Le résultat arrive par <see cref="TakeCompleted"/>.</summary>
    public void Start()
    {
        if (IsBuilding)
            return;
        log.Info("build", $"Compilation de {project.GameProject}…");
        _running = Task.Run(() => BuildAsync(project.Abs(project.GameProject)));
    }

    /// <summary>Rend le résultat d'un build terminé, une seule fois (à appeler à chaque image).</summary>
    public BuildResult? TakeCompleted()
    {
        if (_running is not { IsCompleted: true } task)
            return null;
        _running = null;
        Last = task.IsCompletedSuccessfully
            ? task.Result
            : new BuildResult(false, [], task.Exception?.InnerException?.Message ?? "échec", TimeSpan.Zero);
        foreach (var d in Last.Diagnostics)
            log.Add(d.IsError ? LogLevel.Error : LogLevel.Warning, "build", $"{Path.GetFileName(d.File)}({d.Line},{d.Column}) {d.Code} : {d.Message}");
        if (Last.Succeeded)
            log.Info("build", $"Compilation réussie en {Last.Duration.TotalSeconds:0.0} s");
        else
            log.Error("build", Last.Diagnostics.Count == 0 ? "Compilation échouée :\n" + ProcessRunner.Tail(Last.Output, 2000) : "Compilation échouée");
        return Last;
    }

    static async Task<BuildResult> BuildAsync(string csproj)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync("dotnet", ["build", csproj, "-c", "Debug", "-nologo", "-v", "q", "-clp:NoSummary"],
            Path.GetDirectoryName(csproj)!, TimeSpan.FromMinutes(5), default);
        return new BuildResult(r.Succeeded, Parse(r.Output), r.Output, sw.Elapsed);
    }

    /// <summary>Lit les lignes <c>fichier(ligne,col): error CS0000: message [projet]</c> de MSBuild, sans doublons.</summary>
    public static IReadOnlyList<BuildDiagnostic> Parse(string output)
    {
        var seen = new HashSet<string>();
        var list = new List<BuildDiagnostic>();
        foreach (Match m in DiagnosticRegex().Matches(output))
        {
            var key = m.Value.Trim();
            if (!seen.Add(key))
                continue;
            list.Add(new BuildDiagnostic(m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value),
                m.Groups["kind"].Value == "error", m.Groups["code"].Value, m.Groups["msg"].Value.Trim()));
        }
        return list;
    }

    [GeneratedRegex(@"^(?<file>[^\r\n(]+)\((?<line>\d+),(?<col>\d+)\):\s*(?<kind>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>[^\r\n\[]*)", RegexOptions.Multiline)]
    private static partial Regex DiagnosticRegex();
}
