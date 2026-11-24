using System.Text.Json.Nodes;
using Poiesis.Editor;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Tests;

public sealed class ContentLibraryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "poiesis-content-" + Guid.NewGuid().ToString("N"));
    readonly ContentLibrary _library;
    readonly ProjectConfig _project;
    readonly SceneSerializer _serializer;

    public ContentLibraryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "content", "squads"));
        Directory.CreateDirectory(Path.Combine(_root, "content", "scenarios"));
        File.WriteAllText(Path.Combine(_root, "poiesis.project.json"),
            """{ "name": "T", "gameProject": "a.csproj", "gameAssembly": "a.dll", "startScene": "content/scenarios/battle.scene.json" }""");
        _project = ProjectConfig.Load(_root);
        _library = new ContentLibrary(_project);
        var registry = new NodeTypeRegistry();
        registry.Register(typeof(SquadNode));
        _serializer = new SceneSerializer(registry, _project.ContentRoot);

        var wing = new SceneRoot { Name = "Escadre" };
        wing.AddChild(new SquadNode { Name = "Tête", Count = 3 });
        Write("squads/wing.scene.json", wing);
        var battle = new SceneRoot { Name = "Bataille" };
        var instance = battle.AddChild(_serializer.Instantiate("squads/wing.scene.json", Guid.NewGuid()));
        ((SquadNode)instance.Children[0]).Count = 6;
        Write("scenarios/battle.scene.json", battle);
        File.WriteAllText(Path.Combine(_project.ContentRoot, "squads", "red.squad.json"), """{ "id": "red" }""");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void Write(string rel, Node root) => File.WriteAllText(Path.Combine(_project.ContentRoot, rel), _serializer.Write(root));

    Node ReadBattle(string rel = "scenarios/battle.scene.json") =>
        _serializer.Read(File.ReadAllText(Path.Combine(_project.ContentRoot, rel)));

    [Fact]
    public void The_tree_lists_folders_then_files_by_kind()
    {
        var tree = _library.Tree();
        Assert.Equal(["scenarios", "squads"], tree.Children.Select(c => c.Name));
        var squads = tree.Children[1];
        Assert.Equal([ContentKind.Data, ContentKind.Scene], squads.Children.Select(c => c.Kind));
        Assert.Equal("squads/wing.scene.json", squads.Children[1].Path);
    }

    [Fact]
    public void Moving_a_scene_rewrites_the_scenes_that_instantiate_it()
    {
        Assert.Equal(["scenarios/battle.scene.json"], _library.FindReferences("squads/wing.scene.json"));

        var changed = _library.Move("squads/wing.scene.json", "wings/left.scene.json");

        Assert.Equal(["scenarios/battle.scene.json"], changed);
        var battle = ReadBattle();
        Assert.Equal("wings/left.scene.json", battle.Children[0].InstanceOf);
        Assert.Equal(6, ((SquadNode)battle.Children[0].Children[0]).Count);   // la surcharge survit
        Assert.Empty(_library.FindReferences("squads/wing.scene.json"));
    }

    [Fact]
    public void Renaming_a_folder_rewrites_references_and_the_start_scene()
    {
        _library.Move("squads", "fleets");
        _library.Move("scenarios", "battles");

        Assert.Equal("fleets/wing.scene.json", ReadBattle("battles/battle.scene.json").Children[0].InstanceOf);
        Assert.Equal("content/battles/battle.scene.json", _project.StartScene);
        Assert.Equal("content/battles/battle.scene.json", ProjectConfig.Load(_root).StartScene);
        Assert.Throws<InvalidOperationException>(() => _library.Move("fleets", "fleets/inner"));
        Assert.Throws<IOException>(() => _library.Move("fleets", "battles"));
    }

    [Fact]
    public void Create_duplicate_and_delete()
    {
        var folder = _library.CreateFolder("", "systems");
        var scene = _library.CreateScene(folder, "wolf", _serializer.Write(new SceneRoot { Name = "Wolf" }));
        Assert.Equal("systems/wolf.scene.json", scene);
        Assert.Throws<IOException>(() => _library.CreateScene(folder, "wolf", "{}"));
        Assert.Throws<ArgumentException>(() => _library.CreateFolder("", "../dehors"));

        var copy = _library.Duplicate("squads/wing.scene.json");
        Assert.Equal("squads/wing-copie.scene.json", copy);
        var original = JsonNode.Parse(File.ReadAllText(_library.Abs("squads/wing.scene.json")))!;
        var duplicated = JsonNode.Parse(File.ReadAllText(_library.Abs(copy)))!;
        Assert.NotEqual(original["id"]!.GetValue<string>(), duplicated["id"]!.GetValue<string>());
        Assert.Equal("squads/red-copie.squad.json", _library.Duplicate("squads/red.squad.json"));

        _library.Delete(folder);
        Assert.False(Directory.Exists(_library.Abs("systems")));
        Assert.Throws<InvalidOperationException>(() => _library.Delete(""));
    }
}
