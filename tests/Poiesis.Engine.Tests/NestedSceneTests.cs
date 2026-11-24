using System.Text.Json.Nodes;
using Brutal.Numerics;
using Poiesis.Editor;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Tests;

/// <summary>Scènes imbriquées : instances, surcharges, identifiants, boucles, branche enregistrée comme scène.</summary>
public sealed class NestedSceneTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "poiesis-nested-" + Guid.NewGuid().ToString("N"));
    readonly NodeTypeRegistry _registry = new();
    readonly SceneSerializer _serializer;

    public NestedSceneTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "content", "squads"));
        _registry.Register(typeof(SquadNode));
        _serializer = new SceneSerializer(_registry, Path.Combine(_root, "content"));

        // Une escadre type : une racine et deux escouades.
        var template = new SceneRoot { Name = "Escadre" };
        template.AddChild(new SquadNode { Name = "Tête", Count = 3, Position = new float3(0, 0, 10) });
        template.AddChild(new SquadNode { Name = "Queue", Count = 2, Position = new float3(0, 0, -10) });
        WriteScene("squads/wing.scene.json", template);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void WriteScene(string relative, Node root) =>
        File.WriteAllText(Path.Combine(_root, "content", relative), _serializer.Write(root));

    Node Scenario(params Node[] children)
    {
        var scene = new SceneRoot { Name = "Scénario" };
        foreach (var c in children)
            scene.AddChild(c);
        return scene;
    }

    [Fact]
    public void An_instance_saves_only_what_differs_from_its_source()
    {
        var a = _serializer.Instantiate("squads/wing.scene.json", Guid.NewGuid());
        var b = _serializer.Instantiate("squads/wing.scene.json", Guid.NewGuid());
        a.Name = "Escadre rouge";
        ((SquadNode)b.Children[0]).Count = 7;
        b.Children[1].Name = "Arrière-garde";

        var json = JsonNode.Parse(_serializer.Write(Scenario(a, b)))!;
        var children = json["children"]!.AsArray();
        Assert.Equal("squads/wing.scene.json", children[0]!["instance"]!.GetValue<string>());
        Assert.Equal("Escadre rouge", children[0]!["name"]!.GetValue<string>());
        Assert.Null(children[0]!["overrides"]);                 // seul le nom de l'instance change
        Assert.Null(children[0]!["children"]);                  // les enfants viennent de la source
        var overrides = children[1]!["overrides"]!.AsObject();
        Assert.Equal(2, overrides.Count);
        Assert.Contains(overrides, o => o.Value!["props"]?["count"]?.GetValue<int>() == 7 && o.Value["props"]!.AsObject().Count == 1);
        Assert.Contains(overrides, o => o.Value!["name"]?.GetValue<string>() == "Arrière-garde");

        // Relue, la scène retrouve les surcharges et les mêmes identifiants.
        var read = _serializer.Read(json.ToJsonString());
        var rb = read.Children[1];
        Assert.Equal("squads/wing.scene.json", rb.InstanceOf);
        Assert.Equal(7, ((SquadNode)rb.Children[0]).Count);
        Assert.Equal("Arrière-garde", rb.Children[1].Name);
        Assert.Equal(b.DescendantsAndSelf().Select(n => n.Id), rb.DescendantsAndSelf().Select(n => n.Id));
    }

    [Fact]
    public void Two_instances_of_a_scene_share_no_id_and_ids_are_stable()
    {
        var id = Guid.NewGuid();
        var a = _serializer.Instantiate("squads/wing.scene.json", id);
        var b = _serializer.Instantiate("squads/wing.scene.json", Guid.NewGuid());
        Assert.Empty(a.DescendantsAndSelf().Select(n => n.Id).Intersect(b.DescendantsAndSelf().Select(n => n.Id)));
        Assert.Equal(a.DescendantsAndSelf().Select(n => n.Id), _serializer.Instantiate("squads/wing.scene.json", id).DescendantsAndSelf().Select(n => n.Id));
        Assert.Equal(id, a.Id);
        Assert.All(a.Children, c => Assert.Equal(a, c.OwningInstance));
        Assert.False(a.HasEditableChildren);
    }

    [Fact]
    public void Changing_the_source_changes_every_instance_except_its_overrides()
    {
        var a = _serializer.Instantiate("squads/wing.scene.json", Guid.NewGuid());
        ((SquadNode)a.Children[0]).Count = 9;
        var json = _serializer.Write(Scenario(a));

        var source = _serializer.Read(File.ReadAllText(Path.Combine(_root, "content", "squads", "wing.scene.json")));
        ((SquadNode)source.Children[0]).Count = 4;
        ((SquadNode)source.Children[1]).Count = 5;
        WriteScene("squads/wing.scene.json", source);

        var read = _serializer.Read(json).Children[0];
        Assert.Equal(9, ((SquadNode)read.Children[0]).Count);   // surchargé
        Assert.Equal(5, ((SquadNode)read.Children[1]).Count);   // suit la source
    }

    [Fact]
    public void Instances_nest_and_a_scene_cannot_instantiate_itself()
    {
        var group = new SceneRoot { Name = "Groupe" };
        group.AddChild(_serializer.Instantiate("squads/wing.scene.json", Guid.NewGuid()));
        WriteScene("squads/group.scene.json", group);

        var outer = _serializer.Instantiate("squads/group.scene.json", Guid.NewGuid());
        var innerSquad = (SquadNode)outer.Children[0].Children[1];
        innerSquad.Count = 11;
        var read = _serializer.Read(_serializer.Write(Scenario(outer))).Children[0];
        Assert.Equal(11, ((SquadNode)read.Children[0].Children[1]).Count);
        Assert.Equal("squads/wing.scene.json", read.Children[0].InstanceOf);

        Assert.True(_serializer.DependsOn("squads/group.scene.json", "squads/wing.scene.json"));
        Assert.False(_serializer.DependsOn("squads/wing.scene.json", "squads/group.scene.json"));

        // wing qui instancierait group, qui instancie wing : refusé à la lecture.
        var loop = new SceneRoot();
        loop.AddChild(_serializer.Instantiate("squads/group.scene.json", Guid.NewGuid()));
        var loopJson = _serializer.Write(loop);
        File.WriteAllText(Path.Combine(_root, "content", "squads", "wing.scene.json"), loopJson);
        var ex = Assert.Throws<InvalidDataException>(() => _serializer.Instantiate("squads/group.scene.json", Guid.NewGuid()));
        Assert.Contains("boucle", ex.Message);
    }

    [Fact]
    public void A_branch_becomes_a_reusable_scene_and_the_editor_shows_and_reverts_overrides()
    {
        File.WriteAllText(Path.Combine(_root, "poiesis.project.json"), "{}");
        using var session = new EditorSession(_root);
        session.NodeTypes.Register(typeof(SquadNode));
        var squad = session.Scene.AddChild(new SquadNode { Name = "Garde", Count = 4 });
        var id = squad.Id;

        var instance = session.SaveBranchAsScene(squad, "squads/guard.scene.json");
        Assert.Equal(id, instance.Id);
        Assert.Equal("squads/guard.scene.json", instance.InstanceOf);
        Assert.Same(instance, session.Scene.Children[0]);

        // Une surcharge se voit par rapport à la source, et se rétablit.
        var count = typeof(SquadNode).GetProperty(nameof(SquadNode.Count))!;
        session.History.Execute(new SetPropertyCommand(instance, count, 6));
        var baseline = (SquadNode)session.InstanceBaseline(instance)!;
        Assert.Equal(4, baseline.Count);
        session.History.Execute(new SetPropertyCommand(instance, count, baseline.Count));
        Assert.Null(JsonNode.Parse(session.Serializer.Write(session.Scene))!["children"]![0]!["overrides"]);

        // Annuler revient au nœud d'origine ; « rendre local » détache l'instance.
        session.History.Undo();
        session.History.Undo();
        session.History.Undo();
        Assert.Same(squad, session.Scene.Children[0]);
        session.History.Redo();
        session.History.Execute(new MakeLocalCommand(session.Scene.Children[0]));
        Assert.Null(session.Scene.Children[0].InstanceOf);
        Assert.Equal("test.squad", JsonNode.Parse(session.Serializer.Write(session.Scene))!["children"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Duplicating_an_instance_keeps_its_overrides_with_new_ids()
    {
        File.WriteAllText(Path.Combine(_root, "poiesis.project.json"), "{}");
        using var session = new EditorSession(_root);
        session.NodeTypes.Register(typeof(SquadNode));
        var a = session.InstantiateScene("squads/wing.scene.json", session.Scene);
        ((SquadNode)a.Children[0]).Count = 8;

        var copy = session.CloneNode(a);
        Assert.Equal("squads/wing.scene.json", copy.InstanceOf);
        Assert.Equal(8, ((SquadNode)copy.Children[0]).Count);
        Assert.Empty(a.DescendantsAndSelf().Select(n => n.Id).Intersect(copy.DescendantsAndSelf().Select(n => n.Id)));
        Assert.Throws<InvalidOperationException>(() => session.InstantiateScene("squads/wing.scene.json", a));
    }
}

public sealed class ClipboardTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "poiesis-clip-" + Guid.NewGuid().ToString("N"));

    public ClipboardTests() => Directory.CreateDirectory(Path.Combine(_root, "content"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    EditorSession Session()
    {
        var s = new EditorSession(_root);
        s.NodeTypes.Register(typeof(SquadNode));
        return s;
    }

    [Fact]
    public void Nodes_copied_in_one_scene_paste_into_another_with_new_ids_after_the_selection()
    {
        using var a = Session();
        var squad = a.Scene.AddChild(new SquadNode { Name = "Garde", Count = 5 });
        squad.AddChild(new SquadNode { Name = "Éclaireurs" });
        var text = a.CopyNode(squad);
        Assert.True(EditorSession.IsNodeClipboard(text));
        Assert.False(EditorSession.IsNodeClipboard("bonjour"));

        using var b = Session();
        var first = b.Scene.AddChild(new SquadNode { Name = "Un" });
        b.Scene.AddChild(new SquadNode { Name = "Deux" });
        var pasted = b.Paste(text, first);

        var copy = Assert.Single(pasted);
        Assert.Equal(["Un", "Garde", "Deux"], b.Scene.Children.Select(c => c.Name));
        Assert.Equal(5, ((SquadNode)copy).Count);
        Assert.Equal("Éclaireurs", copy.Children[0].Name);
        Assert.NotEqual(squad.Id, copy.Id);
        Assert.NotEqual(squad.Children[0].Id, copy.Children[0].Id);
        Assert.Same(copy, b.Selection);

        b.History.Undo();
        Assert.Equal(2, b.Scene.Children.Count);
        Assert.Throws<InvalidDataException>(() => b.Paste("{\"kind\":\"autre\"}", null));
    }
}
