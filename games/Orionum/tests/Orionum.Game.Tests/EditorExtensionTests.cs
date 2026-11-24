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

        Assert.Single(types);
        Assert.Equal(["Ordre de bataille"], editor.Panels.Select(p => p.Title));
        Assert.Equal(["FleetNode"], editor.Inspectors.Select(i => i.NodeType.Name));
        Assert.Equal(["BodyNode", "FleetNode"], editor.Gizmos.Select(g => g.NodeType.Name));
        Assert.Equal(["Orionum/Aligner les flottes face à face"], editor.Menus.Select(m => m.Path));
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
        public void Log(string message) { }
        public void Warn(string message) { }
    }
}
