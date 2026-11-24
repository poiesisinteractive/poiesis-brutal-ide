using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Poiesis.AI.Agent;

namespace Poiesis.Editor;

public enum ExportTarget { WindowsX64, LinuxX64 }

public sealed record ExportResult(bool Succeeded, string Folder, string? Archive, string Message);

/// <summary>
/// Exporte un jeu autonome : le joueur Poiesis publié pour la plateforme (runtime .NET compris, renommé au nom du jeu),
/// la DLL du jeu compilée en Release, son contenu, <c>game.json</c>, le <c>NOTICE.txt</c> et le logo de BRUTAL,
/// puis une archive zip à distribuer.
/// </summary>
public sealed class Exporter(ProjectConfig project, LogBuffer log)
{
    Task<ExportResult>? _running;

    public bool IsExporting => _running is { IsCompleted: false };

    public static string Rid(ExportTarget t) => t == ExportTarget.WindowsX64 ? "win-x64" : "linux-x64";

    public static string Label(ExportTarget t) => t == ExportTarget.WindowsX64 ? "Windows x64" : "Linux x64";

    /// <summary>Dossier des exports : <c>&lt;projet&gt;/builds</c>.</summary>
    public string OutputRoot => Path.Combine(project.Root, "builds");

    public void Start(ExportTarget target)
    {
        if (IsExporting)
            return;
        log.Info("export", $"Export {Label(target)} de {project.Name}…");
        _running = Task.Run(() => ExportAsync(target, default));
    }

    public ExportResult? TakeCompleted()
    {
        if (_running is not { IsCompleted: true } task)
            return null;
        _running = null;
        var result = task.IsCompletedSuccessfully ? task.Result : new ExportResult(false, "", null, task.Exception?.InnerException?.Message ?? "échec");
        if (result.Succeeded)
            log.Info("export", result.Message);
        else
            log.Error("export", result.Message);
        return result;
    }

    public async Task<ExportResult> ExportAsync(ExportTarget target, CancellationToken ct)
    {
        var rid = Rid(target);
        var player = FindPlayerProject() ?? throw new FileNotFoundException("Projet du joueur (src/Poiesis.Player) introuvable");
        string safeName = string.Concat(project.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safeName.Length == 0)
            safeName = "Jeu";
        var folder = Path.Combine(OutputRoot, $"{safeName}-{rid}");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        // 1. Le joueur, autonome (runtime .NET compris). Son exécutable prend le nom du jeu : l'hôte .NET garde le
        //    chemin de sa DLL, on peut le renommer (renommer l'assembly, lui, renommerait aussi les projets référencés).
        var publish = await ProcessRunner.RunAsync("dotnet",
            ["publish", player, "-c", "Release", "-r", rid, "--self-contained", "true", "-o", folder, "-nologo", "-v", "q"],
            Path.GetDirectoryName(player)!, TimeSpan.FromMinutes(10), ct);
        if (!publish.Succeeded)
            return new ExportResult(false, folder, null, "Publication du joueur échouée :\n" + ProcessRunner.Tail(publish.Output, 3000));
        string ext = target == ExportTarget.WindowsX64 ? ".exe" : "";
        File.Move(Path.Combine(folder, "Poiesis.Player" + ext), Path.Combine(folder, safeName + ext), overwrite: true);

        // 2. La DLL du jeu, en Release, à part (le moteur est fourni par le joueur).
        var gameOut = Path.Combine(OutputRoot, ".obj", $"{safeName}-game");
        var build = await ProcessRunner.RunAsync("dotnet",
            ["build", project.Abs(project.GameProject), "-c", "Release", "-o", gameOut, "-nologo", "-v", "q"],
            project.Root, TimeSpan.FromMinutes(5), ct);
        if (!build.Succeeded)
            return new ExportResult(false, folder, null, "Compilation du jeu échouée :\n" + ProcessRunner.Tail(build.Output, 3000));
        var gameDir = Directory.CreateDirectory(Path.Combine(folder, "game")).FullName;
        var assemblyName = Path.GetFileName(project.GameAssembly);
        foreach (var file in Directory.EnumerateFiles(gameOut))
        {
            var name = Path.GetFileName(file);
            // Seulement ce qui est propre au jeu : le moteur, BRUTAL et le runtime viennent du joueur.
            if (name.StartsWith("Poiesis.", StringComparison.Ordinal) || name.StartsWith("Brutal.", StringComparison.Ordinal))
                continue;
            if (name.EndsWith(".dll") || name.EndsWith(".pdb") || name.EndsWith(".deps.json"))
                File.Copy(file, Path.Combine(gameDir, name), overwrite: true);
        }
        if (!File.Exists(Path.Combine(gameDir, assemblyName)))
            return new ExportResult(false, folder, null, $"{assemblyName} introuvable après la compilation");

        // 3. Le contenu (scènes, données), sans les fichiers de l'éditeur.
        CopyDirectory(project.ContentRoot, Path.Combine(folder, "content"));

        // 4. Le manifeste lu par le joueur.
        var scene = project.StartScene ?? Directory.EnumerateFiles(project.ContentRoot, "*.scene.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(project.Root, f)).Order(StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException("Aucune scène à jouer");
        var manifest = new JsonObject
        {
            ["name"] = project.Name,
            ["assembly"] = $"game/{assemblyName}",
            ["content"] = "content",
            ["scene"] = scene.Replace('\\', '/'),
            ["credits"] = new JsonArray(project.Credits.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
        };
        File.WriteAllText(Path.Combine(folder, "game.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // Obligations BRUTAL : NOTICE.txt livré (copié par le joueur), logo au démarrage, crédits (F1).
        if (!File.Exists(Path.Combine(folder, "NOTICE.txt")))
            return new ExportResult(false, folder, null, "NOTICE.txt de BRUTAL absent de l'export");

        // 5. L'archive.
        var archive = folder + ".zip";
        if (File.Exists(archive))
            File.Delete(archive);
        ZipFile.CreateFromDirectory(folder, archive, CompressionLevel.Optimal, includeBaseDirectory: true);
        long mb = new FileInfo(archive).Length / (1024 * 1024);
        return new ExportResult(true, folder, archive, $"Export {Label(target)} prêt : {Path.GetRelativePath(project.Root, archive)} ({mb} Mo)");
    }

    /// <summary>Le projet du joueur, cherché en remontant depuis l'éditeur puis depuis le projet de jeu.</summary>
    string? FindPlayerProject()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, project.Root })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Poiesis.Player", "Poiesis.Player.csproj");
                if (File.Exists(candidate))
                    return candidate;
            }
        return null;
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            if (!Path.GetRelativePath(from, dir).StartsWith(".poiesis", StringComparison.Ordinal))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (!rel.StartsWith(".poiesis", StringComparison.Ordinal))
                File.Copy(file, Path.Combine(to, rel), overwrite: true);
        }
    }
}
