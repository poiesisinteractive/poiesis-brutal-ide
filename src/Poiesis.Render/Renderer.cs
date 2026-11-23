using Brutal.Numerics;

namespace Poiesis.Render;

/// <summary>Maillage chargé côté GPU, désigné par un handle opaque.</summary>
public readonly record struct MeshHandle(int Value);

/// <summary>Une instance à dessiner : un maillage, une transformation, une couleur (teinte de faction, émissif).</summary>
public struct MeshInstance
{
    public float4x4 World;
    public float4 Color;
}

public struct Camera
{
    public float3 Position;
    public floatQuat Rotation;
    public float VerticalFovRadians;
    public float Near;
    public float Far;
}

/// <summary>
/// Ce que le jeu et l'éditeur demandent au rendu pour une image. Pensé pour la masse (des milliers de coques) :
/// les instances sont regroupées par maillage et dessinées en instancié.
/// </summary>
public sealed class RenderFrame
{
    readonly Dictionary<MeshHandle, List<MeshInstance>> _batches = new();

    public Camera Camera;

    public float4 ClearColor = new(0.01f, 0.01f, 0.02f, 1f);

    public IReadOnlyDictionary<MeshHandle, List<MeshInstance>> Batches => _batches;

    public void Draw(MeshHandle mesh, in MeshInstance instance)
    {
        if (!_batches.TryGetValue(mesh, out var list))
            _batches[mesh] = list = new List<MeshInstance>();
        list.Add(instance);
    }

    public void Clear()
    {
        foreach (var list in _batches.Values)
            list.Clear();
    }
}

/// <summary>
/// Le rendu, vu de l'éditeur et du jeu. Une seule implémentation est prévue : Vulkan 1.3.
/// Le viewport de l'éditeur rend dans une texture affichée par ImGui (<see cref="ViewportTexture"/>).
/// </summary>
public interface IRenderer : IDisposable
{
    MeshHandle UploadMesh(ReadOnlySpan<float3> positions, ReadOnlySpan<float3> normals, ReadOnlySpan<uint> indices);

    /// <summary>Redimensionne la cible hors écran du viewport (pixels).</summary>
    void ResizeViewport(int2 size);

    /// <summary>Rend une image dans la cible du viewport.</summary>
    void Render(RenderFrame frame);

    /// <summary>Identifiant de texture à passer à <c>ImGui.Image</c> pour afficher le viewport.</summary>
    nint ViewportTexture { get; }
}
