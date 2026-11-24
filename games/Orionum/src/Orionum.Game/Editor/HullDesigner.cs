using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Editor.Api;
using Poiesis.Engine;
using Poiesis.Engine.Assets;
using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Orionum.Game.Editor;

/// <summary>
/// Le concepteur de coques d'Orionum : on choisit une classe de vaisseau, on règle sa coque générée (proue, segments,
/// superstructures, tuyères, quille) avec un aperçu 3D, on pose ses tourelles sur la coque, puis on enregistre dans son
/// <c>*.ship.json</c> (le jeu est rechargé) ou on l'exporte en glTF (texture en KTX2).
/// </summary>
public sealed class HullDesigner : IEditorExtension
{
    const string SettingFile = "orionum.designer.file";

    string? _file;
    JsonObject? _doc;
    ShipClass? _def;
    HullDesign? _design;
    List<WeaponDef> _weapons = new();
    bool _dirty;
    bool _hardpointsChanged;
    string? _error;

    // Aperçu : régénéré quand la coque change.
    HullDesign? _builtFor;
    GeneratedHull? _hull;
    World? _world;
    readonly DrawContext _overlay = new();

    public void Register(EditorRegistry editor)
    {
        editor.Panel("Concepteur de coques", Draw, PanelPlacement.Center);
        editor.MenuItem("Orionum/Poser les tourelles sur la coque", ctx => SnapTurrets());
        editor.MenuItem("Orionum/Enregistrer la coque", Save);
        editor.MenuItem("Orionum/Exporter la coque en glTF", Export);
        editor.Thumbnail("*.ship.json", ShipThumbnail);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    void Draw(IEditorContext ctx)
    {
        var ships = Directory.Exists(Path.Combine(ctx.ContentRoot, "ships"))
            ? Directory.EnumerateFiles(Path.Combine(ctx.ContentRoot, "ships"), "*.ship.json").Order(StringComparer.Ordinal).ToList()
            : [];
        if (_file is null && ctx.GetSetting(SettingFile) is { } saved && File.Exists(Path.Combine(ctx.ContentRoot, saved)))
            Open(ctx, Path.Combine(ctx.ContentRoot, saved));

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Classe"u8, _def is null ? "(choisir)" : $"{_def.DisplayName} ({_def.Id})"))
        {
            foreach (var f in ships)
                if (ImGui.Selectable(Path.GetFileName(f).Replace(".ship.json", ""), f == _file))
                    Open(ctx, f);
            ImGui.EndCombo();
        }
        if (_error is not null)
            ImGui.TextColored(new float4(1, 0.4f, 0.4f, 1), _error);
        if (_def is null || _doc is null)
            return;

        if (_design is null)
        {
            ImGui.TextWrapped($"{_def.DisplayName} utilise une forme de base ({_def.Mesh}){(_def.Model is { } m ? $" ou le modèle {m}" : "")}.");
            if (ImGui.Button("Créer une coque générée"u8))
            {
                _design = DefaultDesign(_def.Role);
                _dirty = true;
            }
            return;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_dirty ? "(modifié)" : "");

        float left = 300;
        if (ImGui.BeginChild("params"u8, new float2(left, 0), ImGuiChildFlags.ResizeX))
            Parameters(ctx);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("view"u8))
            Preview(ctx);
        ImGui.EndChild();
    }

    void Open(IEditorContext ctx, string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            _doc = JsonNode.Parse(text)!.AsObject();
            _def = ValueCodec.Read<ShipClass>(text);
            _design = _def.Design;
            _weapons = [.. _def.Weapons];
            _file = file;
            _dirty = false;
            _hardpointsChanged = false;
            _error = null;
            _builtFor = null;
            ctx.SetSetting(SettingFile, Path.GetRelativePath(ctx.ContentRoot, file).Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            _error = $"{Path.GetFileName(file)} : {ex.Message}";
        }
    }

    /// <summary>Un point de départ par rôle : chaque classe a sa silhouette.</summary>
    public static HullDesign DefaultDesign(Role role) => role switch
    {
        Role.Frigate => new HullDesign { Bow = BowStyle.Pointed, BowLength = 0.35f, Beam = 0.2f, Height = 0.1f, Sections = 1, Engines = 1, EngineSize = 0.08f },
        Role.Cruiser => new HullDesign
        {
            Bow = BowStyle.Split, BowLength = 0.28f, Beam = 0.2f, Height = 0.11f, Sections = 3, Waist = 0.15f, Engines = 3, Keel = 0.04f,
            Superstructures = [new() { At = -0.18f, Length = 0.22f, Width = 0.09f, Height = 0.06f, Tiers = 3 }],
        },
        Role.Battleship => new HullDesign
        {
            Bow = BowStyle.Blunt, BowLength = 0.2f, Beam = 0.26f, Height = 0.16f, Sections = 4, Waist = 0.1f, Engines = 4, Keel = 0.05f,
            Superstructures = [new() { At = -0.1f, Length = 0.3f, Width = 0.12f, Height = 0.08f, Tiers = 3 }, new() { At = 0.18f, Length = 0.1f, Width = 0.08f, Height = 0.04f }],
        },
        Role.Carrier => new HullDesign { Bow = BowStyle.Wedge, BowLength = 0.18f, Beam = 0.34f, Height = 0.12f, Sections = 2, Waist = 0.05f, Engines = 4 },
        _ => new HullDesign { Bow = BowStyle.Pointed, Sections = 3, Engines = 2, Superstructures = [new() { At = -0.15f, Tiers = 2 }] },
    };

    void Parameters(IEditorContext ctx)
    {
        var d = _design!;
        var updated = d;

        if (ImGui.BeginCombo("Proue"u8, d.Bow.ToString()))
        {
            foreach (var style in Enum.GetValues<BowStyle>())
                if (ImGui.Selectable(style.ToString(), style == d.Bow))
                    updated = updated with { Bow = style };
            ImGui.EndCombo();
        }
        updated = updated with
        {
            BowLength = Slider("Longueur de proue", d.BowLength, 0.05f, 0.6f),
            Beam = Slider("Largeur", d.Beam, 0.05f, 0.6f),
            Height = Slider("Hauteur", d.Height, 0.03f, 0.4f),
            Sections = SliderInt("Segments", d.Sections, 1, 8),
            Waist = Slider("Pincement", d.Waist, 0, 0.6f),
            SternTaper = Slider("Poupe effilée", d.SternTaper, 0, 0.8f),
            Engines = SliderInt("Tuyères", d.Engines, 0, 8),
            EngineSize = Slider("Diamètre des tuyères", d.EngineSize, 0.01f, 0.2f),
            Keel = Slider("Quille", d.Keel, 0, 0.2f),
        };

        ImGui.SeparatorText("Superstructures"u8);
        var supers = d.Superstructures.ToList();
        bool superChanged = false;
        for (int i = 0; i < supers.Count; i++)
        {
            ImGui.PushID(i);
            var s = supers[i];
            if (ImGui.TreeNodeEx($"Bloc {i + 1}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var ns = s with
                {
                    At = Slider("Position", s.At, -0.5f, 0.5f),
                    Length = Slider("Longueur", s.Length, 0.02f, 0.6f),
                    Width = Slider("Largeur##s", s.Width, 0.01f, 0.4f),
                    Height = Slider("Hauteur##s", s.Height, 0.01f, 0.3f),
                    Tiers = SliderInt("Étages", s.Tiers, 1, 5),
                };
                if (ns != s)
                {
                    supers[i] = ns;
                    superChanged = true;
                }
                if (ImGui.SmallButton("Retirer"u8))
                {
                    supers.RemoveAt(i);
                    superChanged = true;
                    i--;
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        if (ImGui.SmallButton("+ Superstructure"u8))
        {
            supers.Add(new Superstructure { At = 0, Tiers = 1 });
            superChanged = true;
        }
        if (superChanged)
            updated = updated with { Superstructures = supers };

        if (!ReferenceEquals(updated, d) && !SameDesign(updated, d))
        {
            _design = updated;
            _dirty = true;
        }

        ImGui.SeparatorText("Tourelles"u8);
        int turrets = _weapons.Sum(w => w.Hardpoints.Sum(h => h.Expand().Count()));
        ImGui.Text($"{_weapons.Count} batteries, {turrets} tourelles");
        if (ImGui.Button("Poser les tourelles sur la coque"u8))
            SnapTurrets();
        ImGui.TextDisabled("Bordées sur les flancs, tourelles sur le pont, défense ponctuelle sur l'enveloppe."u8);

        ImGui.SeparatorText("Fichier"u8);
        ImGui.BeginDisabled(!_dirty);
        if (ImGui.Button("Enregistrer et recharger"u8))
            Save(ctx);
        ImGui.SameLine();
        if (ImGui.Button("Annuler"u8) && _file is not null)
            Open(ctx, _file);
        ImGui.EndDisabled();
        if (ImGui.Button("Exporter en glTF"u8))
            Export(ctx);
        ImGui.TextDisabled($"→ content/models/{_def!.Id}.glb (texture KTX2)");
    }

    /// <summary>Même coque ? Comparée en JSON (les listes de superstructures sont des objets différents à chaque image).</summary>
    static bool SameDesign(HullDesign a, HullDesign b) =>
        JsonNode.DeepEquals(ValueCodec.ToJson(a, typeof(HullDesign)), ValueCodec.ToJson(b, typeof(HullDesign)));

    static float Slider(string label, float value, float min, float max)
    {
        ImGui.SliderFloat(label, ref value, min, max, "%.3f"u8);
        return value;
    }

    static int SliderInt(string label, int value, int min, int max)
    {
        ImGui.SliderInt(label, ref value, min, max);
        return value;
    }

    GeneratedHull CurrentHull()
    {
        if (_hull is null || !ReferenceEquals(_builtFor, _design))
        {
            _hull = HullGenerator.Build(_design!, _def!.LengthMeters);
            _builtFor = _design;
            _world = null;
        }
        return _hull;
    }

    void Preview(IEditorContext ctx)
    {
        var hull = CurrentHull();
        if (_world is null)
        {
            // Une copie du maillage, ramenée au cube unité comme dans le jeu (le maillage généré reste en mètres).
            var mesh = new MeshData();
            mesh.Positions.AddRange(hull.Mesh.Positions);
            mesh.Normals.AddRange(hull.Mesh.Normals);
            mesh.TexCoords.AddRange(hull.Mesh.TexCoords);
            mesh.Indices.AddRange(hull.Mesh.Indices);
            var size = mesh.NormalizeToUnitBox(keepOrigin: true);
            _world = ShipWorld(new MeshAsset("designer:" + _def!.Id + ":" + Guid.NewGuid(), mesh, Catalog.Plating), size, _def.Faction, _def.Mesh);
        }

        // Les tourelles : une croix par emplacement, une couleur par batterie.
        _overlay.Clear();
        float mark = _def!.LengthMeters * 0.012f;
        for (int w = 0; w < _weapons.Count; w++)
        {
            var color = BatteryColor(w);
            bool first = true;
            foreach (var p in _weapons[w].Hardpoints.SelectMany(h => h.Expand()))
            {
                _overlay.Line(p - new float3(mark, 0, 0), p + new float3(mark, 0, 0), color);
                _overlay.Line(p - new float3(0, mark, 0), p + new float3(0, mark, 0), color);
                _overlay.Line(p - new float3(0, 0, mark), p + new float3(0, 0, mark), color);
                if (first)
                    _overlay.Label(p, _weapons[w].Name, color);
                first = false;
            }
        }

        var avail = ImGui.GetContentRegionAvail();
        ctx.View3D("designer:" + _file, _world, new float2(avail.X, MathF.Max(avail.Y - 50, 100)), _overlay);
        var (min, max) = hull.Mesh.Bounds();
        var dims = max - min;
        ImGui.Text($"{dims.Z:0} m × {dims.X:0} m × {dims.Y:0} m · {hull.Mesh.TriangleCount} triangles");
        ImGui.TextDisabled("Glisser : tourner · clic droit : déplacer · molette : zoom · double-clic : recadrer"u8);
    }

    static float4 BatteryColor(int i) => (i % 6) switch
    {
        0 => new float4(1f, 0.45f, 0.35f, 1),
        1 => new float4(0.4f, 0.8f, 1f, 1),
        2 => new float4(1f, 0.85f, 0.3f, 1),
        3 => new float4(0.6f, 1f, 0.5f, 1),
        4 => new float4(0.9f, 0.5f, 1f, 1),
        _ => new float4(1f, 1f, 1f, 1),
    };

    void SnapTurrets()
    {
        if (_def is null || _design is null)
            return;
        _weapons = HullGenerator.SnapHardpoints(_weapons, CurrentHull().Profile);
        _hardpointsChanged = true;
        _dirty = true;
    }

    void Save(IEditorContext ctx)
    {
        if (_doc is null || _file is null || _design is null || !_dirty)
            return;
        _doc["design"] = ValueCodec.ToJson(_design, typeof(HullDesign));
        if (_hardpointsChanged && _doc["weapons"] is JsonArray weapons)
            for (int i = 0; i < Math.Min(weapons.Count, _weapons.Count); i++)
                if (weapons[i] is JsonObject w)
                    w["hardpoints"] = new JsonArray(_weapons[i].Hardpoints.Select(HardpointJson).ToArray());
        File.WriteAllText(_file, _doc.ToJsonString(Json) + "\n");
        _dirty = false;
        _hardpointsChanged = false;
        ctx.Log($"Coque enregistrée : {Path.GetFileName(_file)}. Rechargement du jeu.");
        ctx.RequestReload();
    }

    /// <summary>Un emplacement en JSON, avec son seul champ renseigné (comme dans les fichiers écrits à la main).</summary>
    static JsonNode HardpointJson(HardpointSpec h)
    {
        var o = new JsonObject();
        if (h.Row is { } row)
            o["row"] = new JsonArray(row.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        else if (h.Scatter is { } scatter)
            o["scatter"] = new JsonArray(scatter.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
        else if (h.Point is { } p)
            o["point"] = new JsonArray(p.X, p.Y, p.Z);
        return o;
    }

    void Export(IEditorContext ctx)
    {
        if (_def is null || _design is null)
            return;
        var path = Path.Combine(ctx.ContentRoot, "models", _def.Id + ".glb");
        try
        {
            ModelExporter.SaveGlb(CurrentHull().Mesh, Catalog.Plating, path, _def.Id);
            ctx.Log($"Coque exportée : content/models/{_def.Id}.glb ({new FileInfo(path).Length / 1024} Kio). Pour l'utiliser : « model » dans {Path.GetFileName(_file)}.");
        }
        catch (Exception ex)
        {
            ctx.Warn($"Export glTF impossible : {ex.Message}");
        }
    }

    /// <summary>Un monde d'aperçu : un vaisseau à l'origine, avec son maillage.</summary>
    static World ShipWorld(MeshAsset? asset, float3 size, Faction faction, MeshKind fallback)
    {
        var world = new World();
        var library = new MeshLibrary();
        int id = asset is null ? 0 : library.Add(asset);
        world.SetResource(library);
        var e = world.Create();
        world.Add(e, new Transform { Position = float3.Zero, Rotation = floatQuat.Identity, Scale = 1 });
        world.Add(e, new Renderable { Mesh = fallback, Asset = id, Size = size, Color = Visuals.FactionHull(faction) });
        return world;
    }

    /// <summary>La vignette d'une classe de vaisseau dans le navigateur de contenu.</summary>
    static World? ShipThumbnail(string path, IEditorContext ctx)
    {
        var def = ValueCodec.Read<ShipClass>(File.ReadAllText(path));
        var (asset, size) = Catalog.ShipMesh(def, ctx.ContentRoot);
        return ShipWorld(asset, size, def.Faction, def.Mesh);
    }
}
