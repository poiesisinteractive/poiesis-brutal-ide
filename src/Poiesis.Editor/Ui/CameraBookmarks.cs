using System.Text.Json.Nodes;
using Brutal.Numerics;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>Une vue enregistrée. Propriétés à initialiser (pas de constructeur positionnel) : lue par <see cref="ValueCodec"/>.</summary>
public sealed record CameraBookmark
{
    public float3 Target { get; init; }
    public float Distance { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
}

/// <summary>
/// Les caméras enregistrées du viewport, neuf par scène, gardées dans <c>.poiesis/cameras.json</c> (propres au poste,
/// comme la disposition des panneaux).
/// </summary>
sealed class CameraBookmarks(string projectRoot)
{
    readonly string _path = Path.Combine(projectRoot, ".poiesis", "cameras.json");
    readonly string _contentRoot = Path.Combine(projectRoot, "content");
    JsonObject? _doc;

    string Key(string? scenePath) =>
        scenePath is null ? "(sans fichier)" : Path.GetRelativePath(_contentRoot, scenePath).Replace('\\', '/');

    JsonObject Doc()
    {
        if (_doc is not null)
            return _doc;
        try
        {
            _doc = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _doc = null;
        }
        return _doc ??= new JsonObject();
    }

    public void Save(string? scenePath, int slot, CameraBookmark camera)
    {
        var doc = Doc();
        if (doc[Key(scenePath)] is not JsonObject scene)
            doc[Key(scenePath)] = scene = new JsonObject();
        scene[slot.ToString()] = ValueCodec.ToJson(camera, typeof(CameraBookmark));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, SceneSerializer.ToJsonString(doc) + "\n");
    }

    public CameraBookmark? Get(string? scenePath, int slot) =>
        Doc()[Key(scenePath)]?[slot.ToString()] is { } json ? ValueCodec.FromJson(json, typeof(CameraBookmark)) as CameraBookmark : null;

    public IEnumerable<int> Slots(string? scenePath) =>
        Doc()[Key(scenePath)] is JsonObject scene ? scene.Select(kv => int.TryParse(kv.Key, out var i) ? i : 0).Where(i => i > 0).Order() : [];
}
