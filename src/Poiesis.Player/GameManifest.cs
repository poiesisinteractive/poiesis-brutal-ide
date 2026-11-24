using Poiesis.Engine.Scene;

namespace Poiesis.Player;

/// <summary>
/// <c>game.json</c>, à côté de l'exécutable d'un jeu exporté : ce qu'il faut charger et jouer.
/// Les chemins sont relatifs au dossier du jeu.
/// </summary>
public sealed record GameManifest
{
    public string Name { get; init; } = "Jeu";
    public string Assembly { get; init; } = "";
    public string Content { get; init; } = "content";
    public string Scene { get; init; } = "";

    /// <summary>Lignes de crédits du jeu, affichées avant celles de BRUTAL (F1).</summary>
    public List<string> Credits { get; init; } = new();

    public static GameManifest Load(string path) => ValueCodec.Read<GameManifest>(File.ReadAllText(path));
}
