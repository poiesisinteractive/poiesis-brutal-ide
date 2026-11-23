using Brutal.Numerics;
using Poiesis.Editor;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Tests;

public enum Team { Red, Blue }

[NodeType("test.squad")]
public sealed class SquadNode : Node
{
    public Team Team { get; set; }
    public float3 Position { get; set; }
    public int Count { get; set; } = 1;

    public override void Bake(BakeContext ctx)
    {
        for (int i = 0; i < Count; i++)
        {
            var e = ctx.CreateEntity(this);
            var t = Transform.Identity;
            t.Position = Position;
            ctx.World.Add(e, t);
        }
    }
}

public class SceneTests
{
    static NodeTypeRegistry Registry()
    {
        var r = new NodeTypeRegistry();
        r.Register(typeof(SquadNode));
        return r;
    }

    [Fact]
    public void Scene_round_trips_through_json()
    {
        var serializer = new SceneSerializer(Registry());
        var root = new SceneRoot { Name = "Test" };
        var squad = root.AddChild(new SquadNode { Name = "Alpha", Team = Team.Blue, Position = new float3(1, 2, 3), Count = 4 });

        var json = serializer.Write(root);
        var back = serializer.Read(json);

        Assert.Equal(json, serializer.Write(back));
        var s = Assert.IsType<SquadNode>(Assert.Single(back.Children));
        Assert.Equal(squad.Id, s.Id);
        Assert.Equal(Team.Blue, s.Team);
        Assert.Equal(new float3(1, 2, 3), s.Position);
        Assert.Contains("\"team\": \"blue\"", json);
        Assert.Contains("\"type\": \"test.squad\"", json);
    }

    [Fact]
    public void Baking_creates_entities_without_touching_the_scene()
    {
        var root = new SceneRoot();
        root.AddChild(new SquadNode { Count = 3, Position = new float3(5, 0, 0) });
        var world = new World();

        var ctx = SceneBaker.Bake(root, world);

        Assert.Equal(3, world.EntityCount);
        Assert.Single(ctx.EntitiesByNode);
        Assert.Equal(3, world.Store<Transform>().Count);
    }

    [Fact]
    public void Property_edits_are_undoable()
    {
        var squad = new SquadNode { Count = 1 };
        var history = new UndoStack();
        var prop = typeof(SquadNode).GetProperty(nameof(SquadNode.Count))!;

        history.Execute(new SetPropertyCommand(squad, prop, 7));
        Assert.Equal(7, squad.Count);
        history.Undo();
        Assert.Equal(1, squad.Count);
        history.Redo();
        Assert.Equal(7, squad.Count);
    }
}
