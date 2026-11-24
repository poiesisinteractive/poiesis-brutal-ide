using Brutal.Numerics;
using Poiesis.Editor;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Tests;

[NodeType("test.marker")]
public sealed class MarkerNode : Node
{
    public float3 Position { get; set; }

    [Range(-360, 360)]
    public float Heading { get; set; }

    [Range(1, 50)]
    [ScaleHandle]
    public float Size { get; set; } = 10;
}

public class SceneTransformTests
{
    static (UndoStack History, MarkerNode A, MarkerNode B, MarkerNode C) Three()
    {
        var a = new MarkerNode { Name = "a", Position = new float3(0, 0, 0) };
        var b = new MarkerNode { Name = "b", Position = new float3(100, 5, 40) };
        var c = new MarkerNode { Name = "c", Position = new float3(300, -5, 10) };
        return (new UndoStack(), a, b, c);
    }

    [Fact]
    public void Align_and_distribute_are_single_undoable_commands()
    {
        var (history, a, b, c) = Three();
        history.Execute(SceneTransforms.Align([a, b, c], 1, reference: a)!);
        Assert.All(new[] { a, b, c }, n => Assert.Equal(0, n.Position.Y));

        history.Execute(SceneTransforms.Distribute([c, a, b], 0)!);
        Assert.Equal(150, b.Position.X);       // à mi-chemin entre 0 et 300
        Assert.Equal(300, c.Position.X);       // les extrêmes ne bougent pas

        history.Undo();
        history.Undo();
        Assert.Equal(new float3(100, 5, 40), b.Position);
        Assert.Equal(new float3(300, -5, 10), c.Position);
    }

    [Fact]
    public void A_group_rotates_around_its_center_and_commits_once()
    {
        var (history, a, b, _) = Three();
        b.Position = new float3(100, 0, 0);
        var drag = new TransformDrag([a, b]);
        Assert.Equal(new float3(50, 0, 0), drag.Pivot);

        drag.Rotate(90);   // le cap 0° regarde +Z, 90° regarde +X : une rotation de 90° envoie +X vers -Z
        Assert.True(float3.Distance(new float3(50, 0, 50), a.Position) < 0.01f, $"{a.Position}");
        Assert.True(float3.Distance(new float3(50, 0, -50), b.Position) < 0.01f, $"{b.Position}");
        Assert.Equal(90, a.Heading);

        var cmd = drag.Commit("Tourner")!;
        Assert.Equal(new float3(0, 0, 0), a.Position);   // remis au départ : c'est la commande qui change
        history.Execute(cmd);
        Assert.Equal(new float3(50, 0, -50), b.Position);
        history.Undo();
        Assert.Equal(new float3(100, 0, 0), b.Position);
        Assert.Equal(0, b.Heading);
    }

    [Fact]
    public void A_group_scales_its_spacing_and_its_scale_property()
    {
        var (history, a, b, _) = Three();
        b.Position = new float3(100, 0, 0);
        var drag = new TransformDrag([a, b]);
        drag.Scale(2);
        Assert.Equal(new float3(-50, 0, 0), a.Position);
        Assert.Equal(new float3(150, 0, 0), b.Position);
        Assert.Equal(20, a.Size);
        history.Execute(drag.Commit("Échelle")!);
        history.Undo();
        Assert.Equal(10, a.Size);
        Assert.Equal(new float3(0, 0, 0), a.Position);
    }

    [Fact]
    public void Scaling_respects_the_property_range()
    {
        var m = new MarkerNode { Size = 40 };
        var drag = new TransformDrag([m]);
        drag.Scale(3);
        Assert.Equal(50, m.Size);
        Assert.Null(new TransformDrag([new MarkerNode()]).Commit("rien"));
    }
}
