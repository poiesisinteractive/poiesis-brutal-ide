using System.Text.Json.Serialization;
using Poiesis.Engine.Ecs;

namespace Poiesis.Engine.Scene;

/// <summary>
/// Nœud de contenu, créé et édité dans l'éditeur : scénario, flotte, escadre, astre, prefab de vaisseau…
/// Les nœuds ne tournent pas : au Play, <see cref="Bake"/> les transforme en entités ECS.
/// Ce qui naît en jeu (projectiles, drones) n'a pas de nœud.
/// </summary>
/// <remarks>
/// Les propriétés publiques en lecture-écriture sont sérialisées et affichées dans l'inspecteur.
/// Un type de nœud s'enregistre auprès du <see cref="NodeTypeRegistry"/> par son nom stable
/// (<see cref="NodeTypeAttribute"/>), qui sert de discriminant dans les fichiers.
/// </remarks>
public abstract class Node
{
    /// <summary>Identifiant stable, utilisé pour les références entre nœuds et les diffs.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    [JsonIgnore]
    public Node? Parent { get; private set; }

    [JsonIgnore]
    public IReadOnlyList<Node> Children => _children;

    readonly List<Node> _children = new();

    public T AddChild<T>(T child) where T : Node
    {
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    public void RemoveChild(Node child)
    {
        if (_children.Remove(child))
            child.Parent = null;
    }

    public IEnumerable<Node> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in _children)
            foreach (var d in child.DescendantsAndSelf())
                yield return d;
    }

    /// <summary>Crée les entités ECS de ce nœud. Les enfants sont cuits ensuite par <see cref="SceneBaker"/>.</summary>
    public virtual void Bake(BakeContext ctx)
    {
    }
}

/// <summary>Nom stable d'un type de nœud dans les fichiers de scène (ex. <c>"orionum.fleet"</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeTypeAttribute(string typeName) : Attribute
{
    public string TypeName { get; } = typeName;
}

/// <summary>Racine d'un fichier de scène.</summary>
[NodeType("scene")]
public sealed class SceneRoot : Node
{
}

/// <summary>Ce qu'un nœud peut faire pendant la cuisson.</summary>
public sealed class BakeContext(World world)
{
    readonly Dictionary<Guid, Entity> _entities = new();

    public World World { get; } = world;

    /// <summary>Entité créée pour un nœud, pour résoudre les références entre nœuds après la cuisson.</summary>
    public IReadOnlyDictionary<Guid, Entity> EntitiesByNode => _entities;

    public Entity CreateEntity(Node owner)
    {
        var e = World.Create();
        _entities.TryAdd(owner.Id, e);
        return e;
    }
}

public static class SceneBaker
{
    /// <summary>Cuit un arbre de nœuds dans un monde neuf, parents avant enfants.</summary>
    public static BakeContext Bake(Node root, World world)
    {
        var ctx = new BakeContext(world);
        foreach (var node in root.DescendantsAndSelf())
            node.Bake(ctx);
        return ctx;
    }
}
