using Brutal.Numerics;

namespace Poiesis.Render;

/// <summary>Maillage chargé côté GPU, désigné par un handle opaque.</summary>
public readonly record struct MeshHandle(int Value);

/// <summary>Une instance à dessiner : une transformation et une couleur (teinte de faction, surbrillance).</summary>
public struct MeshInstance
{
    public float4x4 World;
    public float4 Color;
}

/// <summary>Un segment coloré (grille, axes, gizmos, lignes de ciblage).</summary>
public struct LineVertex
{
    public float3 Position;
    public float4 Color;

    public LineVertex(float3 position, float4 color)
    {
        Position = position;
        Color = color;
    }
}

public struct Camera
{
    public float3 Position;
    public float3 Target;
    public float VerticalFovRadians;
    public float Near;
    public float Far;

    public readonly float4x4 View => float4x4.CreateLookAt(Position, Target, new float3(0, 1, 0));

    public readonly float4x4 Projection(float aspect) =>
        float4x4.CreatePerspectiveFieldOfView(VerticalFovRadians, aspect, Near, Far);
}

/// <summary>
/// Ce que le jeu et l'éditeur demandent au rendu pour une image. Pensé pour la masse (des milliers de coques) :
/// les instances sont regroupées par maillage et dessinées en instancié.
/// </summary>
public sealed class RenderFrame
{
    readonly Dictionary<MeshHandle, List<MeshInstance>> _batches = new();

    public Camera Camera;

    public float4 ClearColor = new(0.015f, 0.015f, 0.025f, 1f);

    /// <summary>Direction de la lumière principale (vers la lumière), dans le monde.</summary>
    public float3 LightDirection = float3.Normalize(new float3(0.4f, 0.8f, 0.3f));

    public IReadOnlyDictionary<MeshHandle, List<MeshInstance>> Batches => _batches;

    public List<LineVertex> Lines { get; } = new();

    public void Draw(MeshHandle mesh, in MeshInstance instance)
    {
        if (!_batches.TryGetValue(mesh, out var list))
            _batches[mesh] = list = new List<MeshInstance>();
        list.Add(instance);
    }

    public void Line(float3 a, float3 b, float4 color)
    {
        Lines.Add(new LineVertex(a, color));
        Lines.Add(new LineVertex(b, color));
    }

    public void Clear()
    {
        foreach (var list in _batches.Values)
            list.Clear();
        Lines.Clear();
    }
}
