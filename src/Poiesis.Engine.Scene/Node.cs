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

    /// <summary>
    /// Chemin (relatif à <c>content/</c>) de la scène dont ce nœud est l'instance, ou <c>null</c>.
    /// Ses enfants viennent alors de cette scène : on ne change pas leur structure, seulement leurs propriétés (surcharges).
    /// </summary>
    [JsonIgnore]
    public string? InstanceOf { get; set; }

    /// <summary>L'instance de scène la plus proche qui contient ce nœud (hors lui-même), ou <c>null</c>.</summary>
    [JsonIgnore]
    public Node? OwningInstance
    {
        get
        {
            for (var n = Parent; n is not null; n = n.Parent)
                if (n.InstanceOf is not null)
                    return n;
            return null;
        }
    }

    /// <summary>
    /// Vrai si on peut ajouter, retirer ou déplacer des enfants de ce nœud : il n'est ni une instance,
    /// ni dans une instance (la structure vient alors de la scène source).
    /// </summary>
    [JsonIgnore]
    public bool HasEditableChildren => InstanceOf is null && OwningInstance is null;

    public T AddChild<T>(T child) where T : Node => InsertChild(_children.Count, child);

    /// <summary>Insère un enfant à une position donnée (bornée).</summary>
    public T InsertChild<T>(int index, T child) where T : Node
    {
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Insert(Math.Clamp(index, 0, _children.Count), child);
        return child;
    }

    public int IndexOf(Node child) => _children.IndexOf(child);

    public void RemoveChild(Node child)
    {
        if (_children.Remove(child))
            child.Parent = null;
    }

    /// <summary>Vrai si ce nœud est <paramref name="node"/> ou l'un de ses ancêtres.</summary>
    public bool IsSelfOrAncestorOf(Node node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n == this)
                return true;
        return false;
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

/// <summary>
/// La propriété (une chaîne) désigne un fichier de contenu par son champ <c>id</c> : l'inspecteur propose
/// la liste des fichiers <paramref name="pattern"/> du dossier <paramref name="folder"/> (relatif à <c>content/</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ContentRefAttribute(string folder, string pattern) : Attribute
{
    public string Folder { get; } = folder;
    public string Pattern { get; } = pattern;
}

/// <summary>Bornes et pas de glissement d'une propriété numérique dans l'inspecteur.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RangeAttribute(float min, float max, float speed = 1) : Attribute
{
    public float Min { get; } = min;
    public float Max { get; } = max;
    public float Speed { get; } = speed;
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
    readonly Dictionary<Entity, Guid> _owners = new();

    public World World { get; } = world;

    /// <summary>Première entité créée pour un nœud, pour résoudre les références entre nœuds après la cuisson.</summary>
    public IReadOnlyDictionary<Guid, Entity> EntitiesByNode => _entities;

    /// <summary>Le nœud qui a créé chaque entité : sélection au clic dans le viewport.</summary>
    public IReadOnlyDictionary<Entity, Guid> OwnerOf => _owners;

    public Entity CreateEntity(Node owner)
    {
        var e = World.Create();
        _entities.TryAdd(owner.Id, e);
        _owners[e] = owner.Id;
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
