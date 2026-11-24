using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Poiesis.Engine.Scene;

/// <summary>
/// Lecture et écriture des scènes en JSON lisible, stable et diffable :
/// <code>{ "type": "orionum.fleet", "id": "...", "name": "...", "props": { ... }, "children": [ ... ] }</code>
/// L'ordre des propriétés suit leur déclaration ; l'IA comme git peuvent lire et modifier ces fichiers.
/// Les valeurs passent par <see cref="ValueCodec"/>, qui ne retient aucun type du jeu (hot reload).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scènes imbriquées.</b> Un nœud peut être l'instance d'une autre scène (chemin relatif à <c>content/</c>) :
/// <code>{ "instance": "fleets/coalition.line.scene.json", "id": "...", "name": "...", "overrides": { "&lt;id dans la source&gt;": { "name": "...", "props": { ... } } } }</code>
/// Seules les différences avec la scène source sont enregistrées : modifier la source modifie toutes ses instances,
/// sauf là où une instance la surcharge.
/// </para>
/// <para>
/// Les nœuds d'une instance ont des identifiants dérivés de celui de l'instance et de leur identifiant dans la source
/// (<see cref="InstanceNodeId"/>) : deux instances de la même scène ne partagent aucun identifiant, et les identifiants
/// restent les mêmes d'un chargement à l'autre (sélection, références, diff).
/// </para>
/// </remarks>
public sealed class SceneSerializer(NodeTypeRegistry registry, string? contentRoot = null)
{
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Dossier des contenus, d'où sont lues les scènes instanciées.</summary>
    public string? ContentRoot { get; } = contentRoot is null ? null : Path.GetFullPath(contentRoot);

    public string Write(Node root) => ToJsonString(ToJson(root));

    public static string ToJsonString(JsonNode json) => json.ToJsonString(WriteOptions);

    /// <summary>Lit une scène. <paramref name="scenePath"/> (son fichier, s'il y en a un) sert à refuser une scène qui s'instancierait elle-même.</summary>
    public Node Read(string json, string? scenePath = null)
    {
        var obj = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Scène vide");
        return FromJson(obj, scenePath);
    }

    public Node FromJson(JsonObject obj, string? scenePath = null)
    {
        var loading = new List<string>();
        if (scenePath is not null && ContentRoot is not null)
            loading.Add(RelativePath(scenePath));
        return FromJson(obj, loading);
    }

    /// <summary>Chemin d'un fichier relatif au dossier des contenus, avec des <c>/</c>.</summary>
    public string RelativePath(string path) =>
        ContentRoot is null ? path.Replace('\\', '/') : Path.GetRelativePath(ContentRoot, Path.GetFullPath(path, ContentRoot)).Replace('\\', '/');

    /// <summary>Instancie une scène : sa racine prend l'identifiant <paramref name="instanceId"/>, ses autres nœuds des identifiants dérivés.</summary>
    public Node Instantiate(string path, Guid instanceId, string? scenePath = null)
    {
        var loading = new List<string>();
        if (scenePath is not null)
            loading.Add(RelativePath(scenePath));
        return Instantiate(path, instanceId, loading, out _);
    }

    /// <summary>Les scènes qu'un nœud instancie, directement ou par ses enfants (hors scènes instanciées par ces scènes).</summary>
    public static IEnumerable<string> InstancedScenes(Node node) =>
        node.DescendantsAndSelf().Where(n => n.InstanceOf is not null && n.OwningInstance is null).Select(n => n.InstanceOf!).Distinct();

    /// <summary>
    /// Vrai si la scène <paramref name="path"/> instancie <paramref name="target"/>, directement ou non
    /// (ou est elle-même <paramref name="target"/>) : l'instancier dans <paramref name="target"/> ferait une boucle.
    /// </summary>
    public bool DependsOn(string path, string target)
    {
        path = Normalize(path);
        target = Normalize(target);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var p))
        {
            if (p == target)
                return true;
            if (!seen.Add(p))
                continue;
            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(File.ReadAllText(AbsolutePath(p)))?.AsObject();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                continue;
            }
            foreach (var reference in InstanceReferences(obj))
                stack.Push(Normalize(reference));
        }
        return false;
    }

    /// <summary>Les chemins des scènes instanciées dans un document de scène (champs <c>instance</c>).</summary>
    public static IEnumerable<string> InstanceReferences(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["instance"] is JsonValue v && v.TryGetValue<string>(out var path))
                yield return path;
            if (obj["children"] is JsonArray children)
                foreach (var child in children)
                    foreach (var r in InstanceReferences(child))
                        yield return r;
        }
    }

    static string Normalize(string path) => path.Replace('\\', '/');

    string AbsolutePath(string relative) =>
        Path.Combine(ContentRoot ?? throw new InvalidOperationException("Scène instanciée sans dossier de contenus"), relative);

    /// <summary>
    /// Identifiant d'un nœud de scène instanciée : dérivé de l'instance et de l'identifiant du nœud dans la scène source.
    /// </summary>
    public static Guid InstanceNodeId(Guid instanceId, Guid sourceId)
    {
        Span<byte> bytes = stackalloc byte[32];
        instanceId.TryWriteBytes(bytes[..16]);
        sourceId.TryWriteBytes(bytes[16..]);
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(bytes, hash);
        return new Guid(hash);
    }

    public JsonObject ToJson(Node node)
    {
        if (node.InstanceOf is { } path)
            return InstanceToJson(node, path);

        var obj = new JsonObject
        {
            ["type"] = registry.NameOf(node.GetType()),
            ["id"] = node.Id.ToString(),
            ["name"] = node.Name,
        };
        var props = PropsToJson(node);
        if (props.Count > 0)
            obj["props"] = props;
        if (node.Children.Count > 0)
            obj["children"] = new JsonArray(node.Children.Select(c => (JsonNode)ToJson(c)).ToArray());
        return obj;
    }

    static JsonObject PropsToJson(Node node)
    {
        var props = new JsonObject();
        foreach (var p in EditableProperties(node.GetType()))
            props[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = ValueCodec.ToJson(p.GetValue(node), p.PropertyType);
        return props;
    }

    /// <summary>Une instance : le chemin de sa scène, et ce qui diffère de cette scène, nœud par nœud.</summary>
    JsonObject InstanceToJson(Node instance, string path)
    {
        var obj = new JsonObject
        {
            ["instance"] = path,
            ["id"] = instance.Id.ToString(),
            ["name"] = instance.Name,
        };

        var baseline = Instantiate(path, instance.Id, [], out var sourceIds);
        var current = instance.DescendantsAndSelf().ToDictionary(n => n.Id);
        var overrides = new JsonObject();
        foreach (var b in baseline.DescendantsAndSelf())
        {
            if (!current.TryGetValue(b.Id, out var n) || n.GetType() != b.GetType())
                continue;
            var entry = new JsonObject();
            if (n != instance && n.Name != b.Name)
                entry["name"] = n.Name;
            var props = new JsonObject();
            foreach (var p in EditableProperties(n.GetType()))
            {
                var value = ValueCodec.ToJson(p.GetValue(n), p.PropertyType);
                var original = ValueCodec.ToJson(p.GetValue(b), p.PropertyType);
                if (!JsonNode.DeepEquals(value, original))
                    props[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = value;
            }
            if (props.Count > 0)
                entry["props"] = props;
            if (entry.Count > 0)
                overrides[sourceIds[b.Id].ToString()] = entry;
        }
        if (overrides.Count > 0)
            obj["overrides"] = overrides;
        return obj;
    }

    Node FromJson(JsonObject obj, List<string> loading)
    {
        if (obj["instance"] is { } instancePath)
            return InstanceFromJson(obj, instancePath.GetValue<string>(), loading);

        var typeName = obj["type"]?.GetValue<string>() ?? throw new JsonException("Nœud sans \"type\"");
        var type = registry.Resolve(typeName);
        var node = (Node)(Activator.CreateInstance(type) ?? throw new JsonException($"Impossible d'instancier {type}"));
        if (obj["id"] is { } id)
            node.Id = Guid.Parse(id.GetValue<string>());
        node.Name = obj["name"]?.GetValue<string>() ?? "";

        if (obj["props"] is JsonObject props)
            ApplyProps(node, props);

        if (obj["children"] is JsonArray children)
            foreach (var child in children)
                node.AddChild(FromJson(child!.AsObject(), loading));
        return node;
    }

    static void ApplyProps(Node node, JsonObject props)
    {
        foreach (var p in EditableProperties(node.GetType()))
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            if (props[key] is { } value)
                p.SetValue(node, ValueCodec.FromJson(value, p.PropertyType));
        }
    }

    Node InstanceFromJson(JsonObject obj, string path, List<string> loading)
    {
        var id = obj["id"] is { } idValue ? Guid.Parse(idValue.GetValue<string>()) : Guid.NewGuid();
        var node = Instantiate(path, id, loading, out var sourceIds);
        if (obj["name"] is { } name)
            node.Name = name.GetValue<string>();

        if (obj["overrides"] is JsonObject overrides)
        {
            var bySource = node.DescendantsAndSelf().ToDictionary(n => sourceIds[n.Id]);
            foreach (var (key, value) in overrides)
            {
                // Un nœud disparu de la source : sa surcharge est ignorée (et perdue au prochain enregistrement).
                if (!Guid.TryParse(key, out var sourceId) || !bySource.TryGetValue(sourceId, out var target) || value is not JsonObject entry)
                    continue;
                if (target != node && entry["name"] is { } n)
                    target.Name = n.GetValue<string>();
                if (entry["props"] is JsonObject props)
                    ApplyProps(target, props);
            }
        }
        return node;
    }

    /// <summary>
    /// Charge la scène <paramref name="path"/> comme instance : la racine prend <paramref name="instanceId"/>,
    /// les autres nœuds <see cref="InstanceNodeId"/>. <paramref name="sourceIds"/> donne l'identifiant de chacun dans la source.
    /// </summary>
    Node Instantiate(string path, Guid instanceId, List<string> loading, out Dictionary<Guid, Guid> sourceIds)
    {
        path = Normalize(path);
        if (loading.Contains(path, StringComparer.Ordinal))
            throw new InvalidDataException($"Scène instanciée en boucle : {string.Join(" → ", loading.Append(path))}");
        var file = AbsolutePath(path);
        if (!File.Exists(file))
            throw new FileNotFoundException($"Scène instanciée introuvable : {path}", file);

        loading.Add(path);
        Node root;
        try
        {
            root = FromJson(JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new JsonException($"{path} est vide"), loading);
        }
        finally
        {
            loading.RemoveAt(loading.Count - 1);
        }

        sourceIds = new Dictionary<Guid, Guid>();
        foreach (var n in root.DescendantsAndSelf().ToList())
        {
            var sourceId = n.Id;
            n.Id = n == root ? instanceId : InstanceNodeId(instanceId, sourceId);
            sourceIds[n.Id] = sourceId;
        }
        root.InstanceOf = path;
        return root;
    }

    /// <summary>
    /// Copie le JSON d'un nœud avec de nouveaux identifiants (dupliquer, coller). Les clés de surcharge,
    /// qui désignent des nœuds de la scène source, ne changent pas.
    /// </summary>
    public static void RegenerateIds(JsonObject node)
    {
        node["id"] = Guid.NewGuid().ToString();
        if (node["children"] is JsonArray children)
            foreach (var child in children)
                if (child is JsonObject c)
                    RegenerateIds(c);
    }

    /// <summary>Propriétés sérialisées et affichées dans l'inspecteur (hors Id, Name et hiérarchie).</summary>
    public static IEnumerable<PropertyInfo> EditableProperties(Type type) =>
        ValueCodec.DataProperties(type)
            .Where(p => p.Name is not (nameof(Node.Id) or nameof(Node.Name)))
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);
}
