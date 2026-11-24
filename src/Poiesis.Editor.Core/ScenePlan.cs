using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Poiesis.AI;
using Poiesis.AI.Agent;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor;

/// <summary>Une modification proposée par l'IA sur la scène ouverte.</summary>
public abstract record SceneOp
{
    public abstract string Summary { get; }
}

public sealed record AddNodeOp(Guid ParentId, string ParentName, string Type, Guid NodeId, string Name, JsonObject Props) : SceneOp
{
    public override string Summary => $"Ajouter {Name} ({Type}) dans {ParentName}{PropsText(Props)}";

    internal static string PropsText(JsonObject props) =>
        props.Count == 0 ? "" : " : " + string.Join(", ", props.Select(kv => $"{kv.Key} = {kv.Value?.ToJsonString()}"));
}

public sealed record InstantiateOp(Guid ParentId, string ParentName, string Path, Guid NodeId, string Name, JsonObject Props) : SceneOp
{
    public override string Summary => $"Instancier {Path} sous le nom {Name} dans {ParentName}{AddNodeOp.PropsText(Props)}";
}

public sealed record SetPropertyOp(Guid NodeId, string NodeName, string Property, JsonNode? OldValue, JsonNode? Value) : SceneOp
{
    public override string Summary => $"{NodeName}.{Property} : {OldValue?.ToJsonString()} → {Value?.ToJsonString()}";
}

public sealed record RenameOp(Guid NodeId, string OldName, string Name) : SceneOp
{
    public override string Summary => $"Renommer {OldName} en {Name}";
}

public sealed record RemoveOp(Guid NodeId, string Name) : SceneOp
{
    public override string Summary => $"Supprimer {Name}";
}

/// <summary>
/// Ce que l'IA propose de faire à la scène ouverte. L'agent travaille sur une copie de la scène (<see cref="Working"/>) :
/// ses outils la lisent et la modifient, et chaque modification est notée (<see cref="Ops"/>). Rien ne touche la vraie
/// scène avant <see cref="Apply"/>, qui rejoue les modifications retenues en une seule commande annulable.
/// </summary>
public sealed class ScenePlan
{
    readonly EditorSession _session;
    readonly List<SceneOp> _ops = new();

    public ScenePlan(EditorSession session)
    {
        _session = session;
        Working = session.Serializer.FromJson(session.Serializer.ToJson(session.Scene), session.ScenePath);
    }

    /// <summary>La copie de travail de l'agent : la scène avec ses modifications.</summary>
    public Node Working { get; }

    public IReadOnlyList<SceneOp> Ops => _ops;

    /// <summary>Le dernier message de l'agent (son résumé).</summary>
    public string? Message { get; set; }

    Node? Find(Guid id) => Working.DescendantsAndSelf().FirstOrDefault(n => n.Id == id);

    // ----- Outils de l'agent -------------------------------------------------------------------------

    public IReadOnlyList<IAgentTool> Tools() =>
    [
        new DelegateTool(new ToolDefinition("get_scene",
            "Rend la scène ouverte (avec tes modifications) : chaque nœud avec son id, son type, son nom, ses propriétés, " +
            "s'il est une instance (et de quelle scène), et ses enfants. Les positions sont en mètres, les caps en degrés.",
            Schema(new JsonObject())), (_, _) => Task.FromResult(new ToolOutcome(Describe(Working).ToJsonString(Indented)))),
        new DelegateTool(new ToolDefinition("list_node_types",
            "Les types de nœuds qu'on peut ajouter, avec leurs propriétés (type, bornes, description, valeurs possibles).",
            Schema(new JsonObject())), (_, _) => Task.FromResult(new ToolOutcome(DescribeTypes().ToJsonString(Indented)))),
        new DelegateTool(new ToolDefinition("list_scenes",
            "Les scènes réutilisables qu'on peut instancier (flottes types, systèmes…), chemins relatifs à content/.",
            Schema(new JsonObject())), (_, _) => Task.FromResult(new ToolOutcome(string.Join("\n", InstanceableScenes())))),
        new DelegateTool(new ToolDefinition("add_node",
            "Propose d'ajouter un nœud. Rend son id.",
            Schema(new JsonObject
            {
                ["parent_id"] = Str("Id du parent (la racine de la scène pour un nœud de premier niveau)."),
                ["type"] = Str("Type de nœud (voir list_node_types), ex. orionum.fleet."),
                ["name"] = Str("Nom du nœud."),
                ["props"] = Obj("Propriétés, en JSON (ex. {\"position\": [0, 0, 1000], \"heading\": 90})."),
            }, "parent_id", "type", "name")), AddNode),
        new DelegateTool(new ToolDefinition("instantiate_scene",
            "Propose d'instancier une scène réutilisable (voir list_scenes) ; props modifie la racine de l'instance. Rend son id.",
            Schema(new JsonObject
            {
                ["parent_id"] = Str("Id du parent."),
                ["path"] = Str("Chemin de la scène, relatif à content/."),
                ["name"] = Str("Nom de l'instance."),
                ["props"] = Obj("Propriétés de la racine de l'instance (surcharges), en JSON."),
            }, "parent_id", "path", "name")), Instantiate),
        new DelegateTool(new ToolDefinition("set_property",
            "Propose de changer une propriété d'un nœud (y compris un nœud d'instance : c'est alors une surcharge).",
            Schema(new JsonObject
            {
                ["node_id"] = Str("Id du nœud."),
                ["property"] = Str("Nom de la propriété (ex. position, heading)."),
                ["value"] = new JsonObject { ["description"] = "Nouvelle valeur, en JSON (nombre, chaîne, [x, y, z]…)." },
            }, "node_id", "property", "value")), SetProperty),
        new DelegateTool(new ToolDefinition("rename_node", "Propose de renommer un nœud.",
            Schema(new JsonObject { ["node_id"] = Str("Id du nœud."), ["name"] = Str("Nouveau nom.") }, "node_id", "name")), Rename),
        new DelegateTool(new ToolDefinition("remove_node", "Propose de supprimer un nœud (et ses enfants).",
            Schema(new JsonObject { ["node_id"] = Str("Id du nœud.") }, "node_id")), Remove),
    ];

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    static JsonObject Obj(string description) => new() { ["type"] = "object", ["description"] = description };

    static JsonElement Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    static JsonObject Args(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object ? JsonNode.Parse(args.GetRawText())!.AsObject() : new JsonObject();

    static string? Text(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    bool TryNode(JsonObject args, string key, out Node node, out ToolOutcome error)
    {
        node = null!;
        error = null!;
        if (!Guid.TryParse(Text(args, key), out var id) || Find(id) is not { } found)
        {
            error = ToolOutcome.Error($"« {key} » ne désigne aucun nœud de la scène (voir get_scene).");
            return false;
        }
        node = found;
        return true;
    }

    /// <summary>Une propriété éditable par son nom (camelCase ou PascalCase).</summary>
    static PropertyInfo? Property(Type type, string name) =>
        SceneSerializer.EditableProperties(type).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Vérifie et convertit une valeur ; rend un message d'erreur pour l'agent si elle ne convient pas.</summary>
    string? Convert(PropertyInfo p, JsonNode? json, out object? value)
    {
        value = null;
        try
        {
            value = ValueCodec.FromJson(json, p.PropertyType);
        }
        catch (Exception ex)
        {
            return $"{p.Name} : valeur {json?.ToJsonString()} invalide pour {p.PropertyType.Name} ({ex.Message}).";
        }
        if (p.GetCustomAttribute<RangeAttribute>() is { } range && value is IConvertible c and not string and not Enum)
        {
            double v = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
            if (v < range.Min || v > range.Max)
                return $"{p.Name} : {v} hors des bornes [{range.Min} ; {range.Max}].";
        }
        if (p.GetCustomAttribute<ContentRefAttribute>() is { } contentRef && value is string id && id.Length > 0)
        {
            var ids = ContentIds(contentRef);
            if (!ids.Contains(id))
                return $"{p.Name} : « {id} » n'existe pas. Valeurs possibles : {string.Join(", ", ids)}.";
        }
        return null;
    }

    List<string> ContentIds(ContentRefAttribute r)
    {
        var dir = Path.Combine(_session.Serializer.ContentRoot ?? "", r.Folder);
        var ids = new List<string>();
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, r.Pattern).Order(StringComparer.Ordinal))
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file))?["id"]?.GetValue<string>() is { } id)
                        ids.Add(id);
                }
                catch (JsonException)
                {
                }
        return ids;
    }

    /// <summary>Applique des propriétés à un nœud ; rend la première erreur.</summary>
    string? ApplyProps(Node node, JsonObject props)
    {
        foreach (var (key, json) in props)
        {
            if (Property(node.GetType(), key) is not { } p)
                return $"{node.GetType().Name} n'a pas de propriété « {key} ». Propriétés : {string.Join(", ", SceneSerializer.EditableProperties(node.GetType()).Select(x => Camel(x.Name)))}.";
            if (Convert(p, json, out var value) is { } error)
                return error;
            p.SetValue(node, value);
        }
        return null;
    }

    static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    Task<ToolOutcome> AddNode(JsonElement raw, CancellationToken ct)
    {
        var args = Args(raw);
        if (!TryNode(args, "parent_id", out var parent, out var error))
            return Task.FromResult(error);
        if (!parent.HasEditableChildren)
            return Task.FromResult(ToolOutcome.Error($"{parent.Name} vient d'une scène instanciée : on n'y ajoute pas d'enfant."));
        Type type;
        try
        {
            type = _session.NodeTypes.Resolve(Text(args, "type") ?? "");
        }
        catch (KeyNotFoundException ex)
        {
            return Task.FromResult(ToolOutcome.Error($"{ex.Message}. Voir list_node_types."));
        }
        var node = (Node)Activator.CreateInstance(type)!;
        node.Name = Text(args, "name") ?? "";
        var props = args["props"] as JsonObject ?? new JsonObject();
        if (ApplyProps(node, props) is { } bad)
            return Task.FromResult(ToolOutcome.Error(bad));
        parent.AddChild(node);
        _ops.Add(new AddNodeOp(parent.Id, parent.Name, Text(args, "type")!, node.Id, node.Name, (JsonObject)props.DeepClone()));
        return Task.FromResult(new ToolOutcome($"Proposé : {node.Name}, id {node.Id}"));
    }

    Task<ToolOutcome> Instantiate(JsonElement raw, CancellationToken ct)
    {
        var args = Args(raw);
        if (!TryNode(args, "parent_id", out var parent, out var error))
            return Task.FromResult(error);
        if (!parent.HasEditableChildren)
            return Task.FromResult(ToolOutcome.Error($"{parent.Name} vient d'une scène instanciée : on n'y ajoute pas d'enfant."));
        var path = Text(args, "path") ?? "";
        if (!InstanceableScenes().Contains(path))
            return Task.FromResult(ToolOutcome.Error($"« {path} » n'est pas une scène instanciable ici. Voir list_scenes."));
        Node instance;
        try
        {
            instance = _session.Serializer.Instantiate(path, Guid.NewGuid(), _session.ScenePath);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolOutcome.Error(ex.Message));
        }
        instance.Name = Text(args, "name") ?? instance.Name;
        var props = args["props"] as JsonObject ?? new JsonObject();
        if (ApplyProps(instance, props) is { } bad)
            return Task.FromResult(ToolOutcome.Error(bad));
        parent.AddChild(instance);
        _ops.Add(new InstantiateOp(parent.Id, parent.Name, path, instance.Id, instance.Name, (JsonObject)props.DeepClone()));
        return Task.FromResult(new ToolOutcome($"Proposé : instance {instance.Name} de {path}, id {instance.Id}"));
    }

    Task<ToolOutcome> SetProperty(JsonElement raw, CancellationToken ct)
    {
        var args = Args(raw);
        if (!TryNode(args, "node_id", out var node, out var error))
            return Task.FromResult(error);
        var name = Text(args, "property") ?? "";
        if (Property(node.GetType(), name) is not { } p)
            return Task.FromResult(ToolOutcome.Error($"{node.GetType().Name} n'a pas de propriété « {name} »."));
        if (Convert(p, args["value"], out var value) is { } bad)
            return Task.FromResult(ToolOutcome.Error(bad));
        var old = ValueCodec.ToJson(p.GetValue(node), p.PropertyType);
        p.SetValue(node, value);
        _ops.Add(new SetPropertyOp(node.Id, node.Name, p.Name, old, ValueCodec.ToJson(value, p.PropertyType)));
        return Task.FromResult(new ToolOutcome($"Proposé : {node.Name}.{p.Name} = {args["value"]?.ToJsonString()}"));
    }

    Task<ToolOutcome> Rename(JsonElement raw, CancellationToken ct)
    {
        var args = Args(raw);
        if (!TryNode(args, "node_id", out var node, out var error))
            return Task.FromResult(error);
        var old = node.Name;
        node.Name = Text(args, "name") ?? node.Name;
        _ops.Add(new RenameOp(node.Id, old, node.Name));
        return Task.FromResult(new ToolOutcome($"Proposé : {old} devient {node.Name}"));
    }

    Task<ToolOutcome> Remove(JsonElement raw, CancellationToken ct)
    {
        var args = Args(raw);
        if (!TryNode(args, "node_id", out var node, out var error))
            return Task.FromResult(error);
        if (node.Parent is not { HasEditableChildren: true } parent)
            return Task.FromResult(ToolOutcome.Error($"{node.Name} ne se supprime pas (racine, ou nœud d'une scène instanciée)."));
        parent.RemoveChild(node);
        _ops.Add(new RemoveOp(node.Id, node.Name));
        return Task.FromResult(new ToolOutcome($"Proposé : supprimer {node.Name}"));
    }

    List<string> InstanceableScenes()
    {
        var root = _session.Serializer.ContentRoot;
        if (root is null || !Directory.Exists(root))
            return [];
        var current = _session.ScenePath is { } p ? _session.Serializer.RelativePath(p) : null;
        return Directory.EnumerateFiles(root, "*.scene.json", SearchOption.AllDirectories)
            .Select(f => _session.Serializer.RelativePath(f))
            .Where(f => current is null || !_session.Serializer.DependsOn(f, current))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    JsonObject Describe(Node node)
    {
        var obj = new JsonObject
        {
            ["id"] = node.Id.ToString(),
            ["type"] = _session.NodeTypes.All.FirstOrDefault(t => t.Type == node.GetType()).Name ?? node.GetType().Name,
            ["name"] = node.Name,
        };
        if (node.InstanceOf is { } path)
            obj["instanceOf"] = path;
        if (!node.HasEditableChildren)
            obj["note"] = "enfants fixés par la scène source : propriétés modifiables, pas de nouvel enfant";
        var props = new JsonObject();
        foreach (var p in SceneSerializer.EditableProperties(node.GetType()))
            props[Camel(p.Name)] = ValueCodec.ToJson(p.GetValue(node), p.PropertyType);
        if (props.Count > 0)
            obj["props"] = props;
        if (node.Children.Count > 0)
            obj["children"] = new JsonArray(node.Children.Select(c => (JsonNode)Describe(c)).ToArray());
        return obj;
    }

    JsonArray DescribeTypes()
    {
        var types = new JsonArray();
        foreach (var (name, type) in _session.NodeTypes.All.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var props = new JsonArray();
            foreach (var p in SceneSerializer.EditableProperties(type))
            {
                var o = new JsonObject { ["name"] = Camel(p.Name), ["type"] = p.PropertyType.Name };
                if (p.PropertyType.IsEnum)
                    o["values"] = new JsonArray(Enum.GetNames(p.PropertyType).Select(n => (JsonNode)Camel(n)).ToArray());
                if (p.GetCustomAttribute<RangeAttribute>() is { } r)
                    o["range"] = new JsonArray(r.Min, r.Max);
                if (p.GetCustomAttribute<ContentRefAttribute>() is { } c)
                    o["values"] = new JsonArray(ContentIds(c).Select(i => (JsonNode)i).ToArray());
                if (p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is { } d)
                    o["description"] = d.Description;
                props.Add(o);
            }
            types.Add(new JsonObject { ["type"] = name, ["properties"] = props });
        }
        return types;
    }

    // ----- Aperçu ------------------------------------------------------------------------------------

    /// <summary>Les nœuds ajoutés ou modifiés (avec leurs descendants) : dessinés en fantôme dans le viewport.</summary>
    public HashSet<Guid> AffectedIds(IEnumerable<SceneOp>? ops = null)
    {
        var ids = new HashSet<Guid>();
        foreach (var op in ops ?? _ops)
        {
            Guid? id = op switch
            {
                AddNodeOp a => a.NodeId,
                InstantiateOp i => i.NodeId,
                SetPropertyOp s => s.NodeId,
                RenameOp r => r.NodeId,
                _ => null,
            };
            if (id is { } nodeId && Find(nodeId) is { } node)
                foreach (var n in node.DescendantsAndSelf())
                    ids.Add(n.Id);
        }
        return ids;
    }

    public HashSet<Guid> RemovedIds(IEnumerable<SceneOp>? ops = null) =>
        (ops ?? _ops).OfType<RemoveOp>().Select(r => r.NodeId).ToHashSet();

    // ----- Application -------------------------------------------------------------------------------

    /// <summary>
    /// Rejoue les modifications choisies sur la vraie scène, dans l'ordre, en une seule commande annulable. Une
    /// modification dont le nœud n'existe plus (non retenu, supprimé entre-temps) est sautée et signalée.
    /// </summary>
    public (IEditorCommand? Command, IReadOnlyList<string> Skipped) Apply(IEnumerable<SceneOp> chosen)
    {
        var scene = _session.Scene;
        Node? Real(Guid id) => scene.DescendantsAndSelf().FirstOrDefault(n => n.Id == id);
        var done = new List<IEditorCommand>();
        var skipped = new List<string>();
        foreach (var op in chosen)
        {
            IEditorCommand? cmd = null;
            try
            {
                cmd = op switch
                {
                    AddNodeOp a when Real(a.ParentId) is { HasEditableChildren: true } parent => new AddNodeCommand(parent, Create(a)),
                    InstantiateOp i when Real(i.ParentId) is { HasEditableChildren: true } parent => new AddNodeCommand(parent, CreateInstance(i)),
                    SetPropertyOp s when Real(s.NodeId) is { } node && Property(node.GetType(), s.Property) is { } p =>
                        new SetPropertyCommand(node, p, ValueCodec.FromJson(s.Value, p.PropertyType)),
                    RenameOp r when Real(r.NodeId) is { } node => new RenameCommand(node, r.Name),
                    RemoveOp r when Real(r.NodeId) is { Parent.HasEditableChildren: true } node => new RemoveNodeCommand(node),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                skipped.Add($"{op.Summary} ({ex.Message})");
                continue;
            }
            if (cmd is null)
            {
                skipped.Add(op.Summary);
                continue;
            }
            cmd.Do();   // les modifications suivantes peuvent viser un nœud ajouté par celle-ci
            done.Add(cmd);
        }
        // Tout est fait : on défait, et l'historique refait d'un bloc (une seule annulation).
        for (int i = done.Count - 1; i >= 0; i--)
            done[i].Undo();
        if (done.Count == 0)
            return (null, skipped);
        return (new CompositeCommand(done.Count == 1 ? $"IA : {done[0].Label}" : $"IA : {done.Count} modifications de la scène", done), skipped);
    }

    Node Create(AddNodeOp a)
    {
        var node = (Node)Activator.CreateInstance(_session.NodeTypes.Resolve(a.Type))!;
        node.Id = a.NodeId;
        node.Name = a.Name;
        if (ApplyProps(node, a.Props) is { } error)
            throw new InvalidDataException(error);
        return node;
    }

    Node CreateInstance(InstantiateOp i)
    {
        var node = _session.Serializer.Instantiate(i.Path, i.NodeId, _session.ScenePath);
        node.Name = i.Name;
        if (ApplyProps(node, i.Props) is { } error)
            throw new InvalidDataException(error);
        return node;
    }

    /// <summary>Le prompt système de l'agent de scène.</summary>
    public string SystemPrompt()
    {
        var selection = _session.SelectedNodes.Count == 0
            ? "aucune"
            : string.Join(", ", _session.SelectedNodes.Select(n => $"{n.Name} ({n.Id})"));
        return $"""
            Tu aides à composer une scène dans l'éditeur de jeu Poiesis. Le développeur te demande de modifier la scène ouverte.
            Tu ne modifies rien directement : chaque outil (add_node, instantiate_scene, set_property, rename_node, remove_node)
            enregistre une proposition, que le développeur verra en fantôme dans le viewport puis appliquera ou refusera.

            Comment travailler :
            - Commence par get_scene ; consulte list_node_types et list_scenes si tu ajoutes des nœuds.
            - Préfère instancier une scène réutilisable existante (flotte type…) plutôt que tout recréer.
            - Repère : Y vertical, mètres ; un cap (heading) en degrés tourne autour de la verticale, 0° vers +Z, 90° vers +X.
              Pour qu'un nœud en A regarde un point B : heading = atan2(B.x - A.x, B.z - A.z) en degrés.
            - Ne change que ce que la demande implique. Termine par un résumé court : ce que tu proposes, et pourquoi.

            Scène : {(_session.ScenePath is { } p ? Path.GetFileName(p) : "non enregistrée")}. Sélection : {selection}.
            """;
    }
}
