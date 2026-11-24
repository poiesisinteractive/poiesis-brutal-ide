using System.Text.Json;
using System.Text.Json.Nodes;
using Brutal.Numerics;
using Poiesis.Editor;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Tests;

/// <summary>L'IA qui agit sur la scène ouverte : propositions sur une copie, puis application en une commande.</summary>
public sealed class ScenePlanTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "poiesis-plan-" + Guid.NewGuid().ToString("N"));
    readonly EditorSession _session;
    readonly SquadNode _red;

    public ScenePlanTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "content"));
        _session = new EditorSession(_root);
        _session.NodeTypes.Register(typeof(SquadNode));
        _session.NodeTypes.Register(typeof(MarkerNode));
        _red = _session.Scene.AddChild(new SquadNode { Name = "Rouges", Team = Team.Red, Position = new float3(0, 0, 5000) });
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    static async Task<Poiesis.AI.Agent.ToolOutcome> Call(ScenePlan plan, string tool, object args) =>
        await plan.Tools().Single(t => t.Definition.Name == tool).ExecuteAsync(JsonSerializer.SerializeToElement(args), default);

    [Fact]
    public async Task Proposals_change_only_the_working_copy_until_applied_as_one_command()
    {
        var plan = new ScenePlan(_session);
        var scene = JsonNode.Parse((await Call(plan, "get_scene", new { })).Content)!;
        Assert.Equal("Rouges", scene["children"]![0]!["name"]!.GetValue<string>());

        var added = await Call(plan, "add_node", new { parent_id = _session.Scene.Id.ToString(), type = "test.squad", name = "Bleus", props = new { team = "blue", position = new[] { 0, 0, -5000 }, count = 4 } });
        Assert.False(added.IsError, added.Content);
        var bluesId = added.Content[(added.Content.LastIndexOf("id ") + 3)..];
        Assert.False((await Call(plan, "set_property", new { node_id = bluesId, property = "count", value = 6 })).IsError);
        Assert.False((await Call(plan, "set_property", new { node_id = _red.Id.ToString(), property = "position", value = new[] { 100, 0, 5000 } })).IsError);

        // Rien n'a changé dans la vraie scène.
        Assert.Single(_session.Scene.Children);
        Assert.Equal(new float3(0, 0, 5000), _red.Position);
        Assert.Equal(3, plan.Ops.Count);
        Assert.Contains(Guid.Parse(bluesId), plan.AffectedIds());

        var (command, skipped) = plan.Apply(plan.Ops);
        Assert.Empty(skipped);
        Assert.Single(_session.Scene.Children);            // la commande n'est pas encore exécutée
        _session.History.Execute(command!);
        var blues = Assert.IsType<SquadNode>(_session.Scene.Children[1]);
        Assert.Equal((Team.Blue, 6), (blues.Team, blues.Count));
        Assert.Equal(new float3(100, 0, 5000), _red.Position);

        _session.History.Undo();                            // une seule annulation défait tout
        Assert.Single(_session.Scene.Children);
        Assert.Equal(new float3(0, 0, 5000), _red.Position);
    }

    [Fact]
    public async Task Bad_proposals_are_refused_with_a_message_the_agent_can_act_on()
    {
        var plan = new ScenePlan(_session);
        var root = _session.Scene.Id.ToString();
        var badType = await Call(plan, "add_node", new { parent_id = root, type = "test.nothing", name = "x" });
        Assert.True(badType.IsError);
        Assert.Contains("list_node_types", badType.Content);

        var badProp = await Call(plan, "add_node", new { parent_id = root, type = "test.squad", name = "x", props = new { colour = 3 } });
        Assert.True(badProp.IsError);
        Assert.Contains("count", badProp.Content);           // la liste des propriétés existantes

        var marker = await Call(plan, "add_node", new { parent_id = root, type = "test.marker", name = "m", props = new { size = 500 } });
        Assert.True(marker.IsError);
        Assert.Contains("bornes", marker.Content);

        Assert.True((await Call(plan, "set_property", new { node_id = Guid.NewGuid().ToString(), property = "count", value = 1 })).IsError);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public async Task Unchosen_proposals_are_skipped_and_the_rest_still_applies()
    {
        var plan = new ScenePlan(_session);
        var added = await Call(plan, "add_node", new { parent_id = _session.Scene.Id.ToString(), type = "test.squad", name = "Bleus" });
        var id = added.Content[(added.Content.LastIndexOf("id ") + 3)..];
        await Call(plan, "rename_node", new { node_id = id, name = "Azur" });
        await Call(plan, "remove_node", new { node_id = _red.Id.ToString() });

        // Sans l'ajout, le renommage n'a plus de nœud : il est sauté ; la suppression s'applique.
        var (command, skipped) = plan.Apply(plan.Ops.Skip(1));
        Assert.Single(skipped);
        _session.History.Execute(command!);
        Assert.Empty(_session.Scene.Children);
        Assert.Contains(_red.Id, plan.RemovedIds());
    }
}
