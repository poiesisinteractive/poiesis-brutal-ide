using System.Reflection;
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
public sealed class SceneSerializer(NodeTypeRegistry registry)
{
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Write(Node root) => ToJson(root).ToJsonString(WriteOptions);

    public Node Read(string json)
    {
        var obj = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Scène vide");
        return FromJson(obj);
    }

    JsonObject ToJson(Node node)
    {
        var props = new JsonObject();
        foreach (var p in EditableProperties(node.GetType()))
            props[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = ValueCodec.ToJson(p.GetValue(node), p.PropertyType);

        var obj = new JsonObject
        {
            ["type"] = registry.NameOf(node.GetType()),
            ["id"] = node.Id.ToString(),
            ["name"] = node.Name,
        };
        if (props.Count > 0)
            obj["props"] = props;
        if (node.Children.Count > 0)
            obj["children"] = new JsonArray(node.Children.Select(c => (JsonNode)ToJson(c)).ToArray());
        return obj;
    }

    Node FromJson(JsonObject obj)
    {
        var typeName = obj["type"]?.GetValue<string>() ?? throw new JsonException("Nœud sans \"type\"");
        var type = registry.Resolve(typeName);
        var node = (Node)(Activator.CreateInstance(type) ?? throw new JsonException($"Impossible d'instancier {type}"));
        if (obj["id"] is { } id)
            node.Id = Guid.Parse(id.GetValue<string>());
        node.Name = obj["name"]?.GetValue<string>() ?? "";

        if (obj["props"] is JsonObject props)
        {
            foreach (var p in EditableProperties(type))
            {
                var key = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                if (props[key] is { } value)
                    p.SetValue(node, ValueCodec.FromJson(value, p.PropertyType));
            }
        }

        if (obj["children"] is JsonArray children)
            foreach (var child in children)
                node.AddChild(FromJson(child!.AsObject()));
        return node;
    }

    /// <summary>Propriétés sérialisées et affichées dans l'inspecteur (hors Id, Name et hiérarchie).</summary>
    public static IEnumerable<PropertyInfo> EditableProperties(Type type) =>
        ValueCodec.DataProperties(type)
            .Where(p => p.Name is not (nameof(Node.Id) or nameof(Node.Name)))
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);
}
