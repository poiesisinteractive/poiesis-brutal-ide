using System.Text;
using System.Text.Json;

namespace Poiesis.AI.Agent;

/// <summary>
/// Outils de l'agent sur l'espace de travail d'une feature (un worktree git). Tout chemin est résolu
/// sous la racine ; l'agent ne peut ni lire ni écrire ailleurs.
/// </summary>
public sealed class WorkspaceTools(string root, WorkspaceCommands commands)
{
    readonly string _root = Path.GetFullPath(root);

    public IReadOnlyList<IAgentTool> All() =>
    [
        new DelegateTool(new ToolDefinition("list_files", "Liste les fichiers suivis par git sous un dossier du projet (chemins relatifs).",
            DelegateTool.StringArgs(("path", "Dossier relatif à la racine du projet, « . » pour la racine."))), ListFiles),
        new DelegateTool(new ToolDefinition("read_file", "Lit un fichier texte du projet.",
            DelegateTool.StringArgs(("path", "Chemin relatif à la racine du projet."))), ReadFile),
        new DelegateTool(new ToolDefinition("write_file", "Crée ou remplace un fichier texte du projet avec le contenu complet donné.",
            DelegateTool.StringArgs(("path", "Chemin relatif à la racine du projet."), ("content", "Contenu complet du fichier."))), WriteFile),
        new DelegateTool(new ToolDefinition("build", "Compile la solution du jeu et rend les erreurs.", DelegateTool.StringArgs()),
            (_, ct) => Run(commands.Build, ct)),
        new DelegateTool(new ToolDefinition("test", "Lance les tests du jeu et rend le résultat.", DelegateTool.StringArgs()),
            (_, ct) => Run(commands.Test, ct)),
        new DelegateTool(new ToolDefinition("run_headless", "Joue un scénario en sim headless déterministe et rend ses métriques.",
            DelegateTool.StringArgs(("scenario", "Chemin relatif du fichier de scène à jouer."))), RunHeadless),
    ];

    async Task<ToolOutcome> ListFiles(JsonElement args, CancellationToken ct)
    {
        if (!DelegateTool.TryGetString(args, "path", out var rel) || !TryResolve(rel, out var dir))
            return ToolOutcome.Error("Argument « path » invalide.");
        var result = await ProcessRunner.RunAsync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "--", dir], _root, TimeSpan.FromSeconds(30), ct);
        return result.Succeeded ? new ToolOutcome(result.Output) : ToolOutcome.Error(result.Output);
    }

    async Task<ToolOutcome> ReadFile(JsonElement args, CancellationToken ct)
    {
        if (!DelegateTool.TryGetString(args, "path", out var rel) || !TryResolve(rel, out var path))
            return ToolOutcome.Error("Argument « path » invalide.");
        if (!File.Exists(path))
            return ToolOutcome.Error($"Fichier introuvable : {rel}");
        return new ToolOutcome(await File.ReadAllTextAsync(path, ct));
    }

    async Task<ToolOutcome> WriteFile(JsonElement args, CancellationToken ct)
    {
        if (!DelegateTool.TryGetString(args, "path", out var rel) || !TryResolve(rel, out var path))
            return ToolOutcome.Error("Argument « path » invalide.");
        if (!DelegateTool.TryGetString(args, "content", out var content))
            return ToolOutcome.Error("Argument « content » manquant.");
        if (IsProtected(rel))
            return ToolOutcome.Error($"{rel} est protégé : l'agent ne modifie ni le moteur ni la configuration git.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        return new ToolOutcome($"Écrit : {rel} ({content.Length} caractères)");
    }

    Task<ToolOutcome> RunHeadless(JsonElement args, CancellationToken ct)
    {
        if (!DelegateTool.TryGetString(args, "scenario", out var rel) || !TryResolve(rel, out var path))
            return Task.FromResult(ToolOutcome.Error("Argument « scenario » invalide."));
        return Run(commands.Headless(path), ct);
    }

    async Task<ToolOutcome> Run(WorkspaceCommand command, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(command.FileName, command.Args, _root, command.Timeout, ct);
        var text = $"code de sortie {result.ExitCode}\n{ProcessRunner.Tail(result.Output)}";
        return result.Succeeded ? new ToolOutcome(text) : ToolOutcome.Error(text);
    }

    /// <summary>Résout un chemin relatif sous la racine ; refuse tout ce qui en sort.</summary>
    public bool TryResolve(string relative, out string fullPath)
    {
        fullPath = Path.GetFullPath(Path.Combine(_root, relative));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return fullPath == _root || fullPath.StartsWith(rootWithSep, StringComparison.Ordinal);
    }

    static bool IsProtected(string relative)
    {
        var p = relative.Replace('\\', '/').TrimStart('.', '/');
        return p.StartsWith(".git", StringComparison.Ordinal) || p.StartsWith("src/Poiesis.", StringComparison.Ordinal);
    }
}

public sealed record WorkspaceCommand(string FileName, IReadOnlyList<string> Args, TimeSpan Timeout);

/// <summary>Commandes de build, de test et de sim headless du projet de jeu.</summary>
public sealed record WorkspaceCommands(WorkspaceCommand Build, WorkspaceCommand Test, Func<string, WorkspaceCommand> Headless)
{
    public static WorkspaceCommands DotNet(string solution, string headlessProject) => new(
        new WorkspaceCommand("dotnet", ["build", solution, "-c", "Debug", "-nologo", "-clp:ErrorsOnly"], TimeSpan.FromMinutes(5)),
        new WorkspaceCommand("dotnet", ["test", solution, "-nologo", "--logger", "console;verbosity=minimal"], TimeSpan.FromMinutes(10)),
        scenario => new WorkspaceCommand("dotnet", ["run", "--project", headlessProject, "-c", "Release", "--", scenario], TimeSpan.FromMinutes(5)));
}
