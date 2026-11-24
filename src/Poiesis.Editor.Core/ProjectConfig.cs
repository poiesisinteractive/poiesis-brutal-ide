using System.Text.Json;
using System.Text.Json.Nodes;

namespace Poiesis.Editor;

/// <summary>
/// Description d'un projet de jeu, lue dans <c>poiesis.project.json</c> à la racine du projet.
/// Tous les chemins sont relatifs à cette racine.
/// </summary>
public sealed record ProjectConfig
{
    public required string Root { get; init; }
    public required string Name { get; init; }

    /// <summary>Projet C# du gameplay (la DLL rechargée à chaud).</summary>
    public required string GameProject { get; init; }

    /// <summary>DLL produite par <see cref="GameProject"/> en Debug.</summary>
    public required string GameAssembly { get; init; }

    /// <summary>Projet de tests du jeu (garde-fou de l'IA), facultatif.</summary>
    public string? TestProject { get; init; }

    /// <summary>Scène ouverte au démarrage dans l'éditeur, et jouée par le jeu exporté.</summary>
    public string? StartScene { get; init; }

    /// <summary>Lignes de crédits du jeu, affichées dans le jeu exporté (avant celles de BRUTAL).</summary>
    public IReadOnlyList<string> Credits { get; init; } = [];

    public string ContentRoot => Path.Combine(Root, "content");

    public string Abs(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public static ProjectConfig Load(string root)
    {
        root = Path.GetFullPath(root);
        var path = Path.Combine(root, "poiesis.project.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Pas de poiesis.project.json dans {root}", path);
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string Req(string key) => o[key]?.GetValue<string>() ?? throw new JsonException($"poiesis.project.json : « {key} » manquant");
        return new ProjectConfig
        {
            Root = root,
            Name = Req("name"),
            GameProject = Req("gameProject"),
            GameAssembly = Req("gameAssembly"),
            TestProject = o["testProject"]?.GetValue<string>(),
            StartScene = o["startScene"]?.GetValue<string>(),
            Credits = o["credits"] is JsonArray credits ? credits.Select(c => c!.GetValue<string>()).ToList() : [],
        };
    }

    /// <summary>Racine du dépôt git qui contient le projet (pour les worktrees de l'IA).</summary>
    public string? FindGitRoot()
    {
        for (var dir = new DirectoryInfo(Root); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }
}
