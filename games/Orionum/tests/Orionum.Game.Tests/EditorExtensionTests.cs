using Brutal.Numerics;
using Orionum.Game.Editor;
using Poiesis.Editor.Api;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

namespace Orionum.Game.Tests;

/// <summary>Les outils d'éditeur d'Orionum (API d'extension), sans fenêtre.</summary>
public class EditorExtensionTests
{
    [Fact]
    public void The_game_dll_provides_its_editor_extension_through_the_shared_api()
    {
        var registry = new NodeTypeRegistry();
        using var host = new GameModuleHost(registry, GameModuleTests.ContentRoot);
        host.Load(GameModuleTests.GameDll());

        // Comme l'éditeur : les extensions sont trouvées dans la DLL chargée à chaud, et l'API est partagée avec l'hôte.
        var types = host.Assembly!.GetTypes().Where(t => typeof(IEditorExtension).IsAssignableFrom(t)).ToList();
        var editor = new EditorRegistry();
        foreach (var t in types)
            ((IEditorExtension)Activator.CreateInstance(t)!).Register(editor);

        Assert.Equal(["HullDesigner", "OrionumEditor"], types.Select(t => t.Name).Order());
        Assert.Equal(["Concepteur de coques", "Ordre de bataille"], editor.Panels.Select(p => p.Title).Order());
        Assert.Equal(PanelPlacement.Center, editor.Panels.Single(p => p.Title == "Concepteur de coques").Placement);
        Assert.Equal(["FleetNode"], editor.Inspectors.Select(i => i.NodeType.Name));
        Assert.Equal(["BodyNode", "FleetNode"], editor.Gizmos.Select(g => g.NodeType.Name));
        Assert.Contains("Orionum/Aligner les flottes face à face", editor.Menus.Select(m => m.Path));
        Assert.Contains("Orionum/Poser les tourelles sur la coque", editor.Menus.Select(m => m.Path));
        Assert.Equal(["*.ship.json"], editor.Thumbnails.Select(t => t.Pattern));
    }

    [Fact]
    public void Every_bow_style_builds_a_closed_textured_hull_of_the_class_length()
    {
        foreach (var bow in Enum.GetValues<BowStyle>())
        {
            var design = HullDesigner.DefaultDesign(Role.Cruiser) with { Bow = bow };
            var hull = HullGenerator.Build(design, 600);
            var (min, max) = hull.Mesh.Bounds();
            Assert.Equal(300, max.Z, 1f);                        // la proue au bout
            Assert.InRange(min.Z, -340, -300);                   // la poupe, plus les tuyères
            Assert.Equal(hull.Mesh.Positions.Count, hull.Mesh.TexCoords.Count);
            Assert.All(hull.Mesh.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
            Assert.InRange(hull.Profile.HalfWidth(0), 50, 60.5f);   // largeur 0,2 × 600 / 2, moins le pincement
        }
    }

    [Fact]
    public void Snapping_puts_broadsides_on_the_flanks_and_turrets_on_the_deck()
    {
        var hull = HullGenerator.Build(new HullDesign { Beam = 0.2f, Height = 0.1f, Sections = 1, Waist = 0 }, 600);
        var weapons = new List<WeaponDef>
        {
            new() { Name = "bâbord", Mount = Mount.Port, Hardpoints = [new HardpointSpec { Row = [4, 100, 20, -150, 150] }] },
            new() { Name = "tribord", Mount = Mount.Starboard, Hardpoints = [new HardpointSpec { Row = [4, -100, 20, -150, 150] }] },
            new() { Name = "pont", Mount = Mount.Turret, Hardpoints = [new HardpointSpec { Point = new float3(0, 80, 100) }] },
        };

        var snapped = HullGenerator.SnapHardpoints(weapons, hull.Profile);

        Assert.Equal(60, snapped[0].Hardpoints[0].Row![1], 0.5f);
        Assert.Equal(-60, snapped[1].Hardpoints[0].Row![1], 0.5f);
        Assert.Equal(30, snapped[2].Hardpoints[0].Point!.Value.Y, 0.5f);
        Assert.Equal(weapons[0].Hardpoints[0].Row![0], snapped[0].Hardpoints[0].Row![0]);   // le nombre de tourelles ne change pas
    }

    [Fact]
    public void Ship_classes_get_their_generated_hull_or_imported_model()
    {
        var catalog = Catalog.Load(GameModuleTests.ContentRoot);
        var cruiser = catalog.Classes[catalog.IndexOf("coalition.cruiser")];
        var frigate = catalog.Classes[catalog.IndexOf("clans.frigate")];
        var battleship = catalog.Classes[catalog.IndexOf("coalition.battleship")];

        Assert.StartsWith("hull:", catalog.Meshes.Get(cruiser.MeshAsset)!.Key);
        var model = catalog.Meshes.Get(frigate.MeshAsset)!;
        Assert.Equal("model:models/clans.frigate.glb", model.Key);
        Assert.NotNull(model.Texture);                            // la texture KTX2 du modèle, transcodée
        Assert.Equal(frigate.Def.LengthMeters, frigate.RenderSize.Z, frigate.Def.LengthMeters * 0.15f);
        Assert.Equal(0, battleship.MeshAsset);                    // forme de base
    }

    [Fact]
    public void Fleet_strength_scales_with_the_fleet()
    {
        var catalog = Catalog.Load(GameModuleTests.ContentRoot);
        var one = OrionumEditor.Of(catalog, new FleetNode { Composition = "coalition.standard", Scale = 1 });
        var twenty = OrionumEditor.Of(catalog, new FleetNode { Composition = "coalition.standard", Scale = 20 });
        Assert.Equal(25, one.Hulls);
        Assert.Equal(500, twenty.Hulls);
        Assert.Equal(one.HullPoints * 20, twenty.HullPoints, 1f);
        Assert.True(one.Firepower > 0);
    }

    [Fact]
    public void Aligning_fleets_turns_each_toward_the_enemy_in_one_group()
    {
        var scene = new ScenarioNode();
        var a = scene.AddChild(new FleetNode { Faction = Faction.Coalition, Position = new float3(0, 0, 0), Heading = 90 });
        var b = scene.AddChild(new FleetNode { Faction = Faction.Clans, Position = new float3(10_000, 0, 0), Heading = 0 });
        var ctx = new FakeContext(scene);
        var editor = new EditorRegistry();
        new OrionumEditor().Register(editor);

        ctx.Group("aligner", () => editor.Menus[0].Run(ctx));

        Assert.Equal(90, a.Heading, 0.1f);    // déjà tournée vers +X
        Assert.Equal(-90, b.Heading, 0.1f);
        Assert.Equal(1, ctx.Sets);
    }

    sealed class FakeContext(Node scene) : IEditorContext
    {
        public int Sets;
        public Node Scene => scene;
        public Node? Selection { get; set; }
        public string ContentRoot => GameModuleTests.ContentRoot;
        public World? Preview => null;
        public World? Game => null;
        public void Set(Node node, string property, object? value)
        {
            node.GetType().GetProperty(property)!.SetValue(node, value);
            Sets++;
        }
        public void Rename(Node node, string name) => node.Name = name;
        public void Add(Node parent, Node child) => parent.AddChild(child);
        public void Remove(Node node) => node.Parent!.RemoveChild(node);
        public void Group(string label, Action edits) => edits();
        public void PropertyField(Node node, string property) { }
        public bool View3D(string id, World world, Brutal.Numerics.float2 size, Poiesis.Engine.Debug.DrawContext? overlay = null) => false;
        public void RequestReload() { }
        readonly Dictionary<string, string> _settings = new();
        public string? GetSetting(string key) => _settings.GetValueOrDefault(key);
        public void SetSetting(string key, string? value)
        {
            if (value is null)
                _settings.Remove(key);
            else
                _settings[key] = value;
        }
        public void Log(string message) { }
        public void Warn(string message) { }
    }
}
