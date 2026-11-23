using System.Reflection;

namespace Poiesis.Engine.Scene;

/// <summary>
/// Table nom stable ↔ type de nœud. Les modules de jeu y enregistrent leurs nœuds au chargement,
/// et l'en retirent au déchargement (hot reload).
/// </summary>
public sealed class NodeTypeRegistry
{
    readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    readonly Dictionary<Type, string> _byType = new();

    public NodeTypeRegistry()
    {
        Register(typeof(SceneRoot));
    }

    public IEnumerable<(string Name, Type Type)> All => _byName.Select(kv => (kv.Key, kv.Value));

    public void Register(Type type)
    {
        if (!typeof(Node).IsAssignableFrom(type) || type.IsAbstract)
            throw new ArgumentException($"{type} n'est pas un type de nœud concret");
        var attr = type.GetCustomAttribute<NodeTypeAttribute>()
            ?? throw new ArgumentException($"{type} n'a pas d'attribut [NodeType]");
        _byName[attr.TypeName] = type;
        _byType[type] = attr.TypeName;
    }

    /// <summary>Enregistre tous les types de nœuds concrets d'une assembly.</summary>
    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
            if (typeof(Node).IsAssignableFrom(type) && !type.IsAbstract && type.GetCustomAttribute<NodeTypeAttribute>() is not null)
                Register(type);
    }

    public void UnregisterAssembly(Assembly assembly)
    {
        foreach (var (type, name) in _byType.Where(kv => kv.Key.Assembly == assembly).ToList())
        {
            _byType.Remove(type);
            _byName.Remove(name);
        }
    }

    public Type Resolve(string typeName) =>
        _byName.TryGetValue(typeName, out var t) ? t : throw new KeyNotFoundException($"Type de nœud inconnu : {typeName}");

    public string NameOf(Type type) =>
        _byType.TryGetValue(type, out var n) ? n : throw new KeyNotFoundException($"Type de nœud non enregistré : {type}");
}
