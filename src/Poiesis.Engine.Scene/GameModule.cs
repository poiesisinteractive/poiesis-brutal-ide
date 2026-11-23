using Poiesis.Engine.Ecs;

namespace Poiesis.Engine.Scene;

/// <summary>
/// Point d'entrée d'un jeu. L'assembly du jeu (gameplay, écrit à la main ou généré par l'IA)
/// est chargée dans un contexte rechargeable par l'éditeur ; elle expose une seule implémentation de ce contrat.
/// </summary>
/// <remarks>
/// Règle du hot reload : rien de ce que le module crée ne doit être retenu par l'éditeur après
/// <see cref="Unload"/> (pas d'événement statique, pas de thread, pas de cache global), sinon l'assembly
/// ne se décharge pas. L'état de jeu, lui, survit car il est sérialisé (nœuds) ou en structs (ECS).
/// </remarks>
public interface IGameModule
{
    string Name { get; }

    /// <summary>Enregistre les types de nœuds, les ressources par défaut, etc.</summary>
    void Load(GameModuleContext ctx);

    /// <summary>Installe les ressources du monde (données statiques, RNG…) avant la cuisson de la scène.</summary>
    void InitializeWorld(World world);

    /// <summary>Construit l'ordre des systèmes de la sim pour un monde cuit.</summary>
    void BuildSchedule(World world, Schedule schedule);

    void Unload(GameModuleContext ctx);
}

public sealed class GameModuleContext(NodeTypeRegistry nodeTypes, string contentRoot)
{
    public NodeTypeRegistry NodeTypes { get; } = nodeTypes;

    /// <summary>Dossier des contenus du jeu (scènes, définitions JSON).</summary>
    public string ContentRoot { get; } = contentRoot;
}
