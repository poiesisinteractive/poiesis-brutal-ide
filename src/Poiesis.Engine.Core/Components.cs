using Brutal.Numerics;

namespace Poiesis.Engine;

/// <summary>Position et orientation dans le monde. Composant de base partagé par tous les jeux.</summary>
public struct Transform
{
    public float3 Position;
    public floatQuat Rotation;
    public float Scale;

    public static Transform Identity => new() { Rotation = floatQuat.Identity, Scale = 1f };
}

/// <summary>Formes de base que l'éditeur et le rendu savent dessiner sans connaître le jeu.</summary>
public enum MeshKind
{
    Box,
    Sphere,
    /// <summary>Coque effilée, proue vers +Z.</summary>
    Hull,
    /// <summary>Coque massive et anguleuse, proue vers +Z.</summary>
    HeavyHull,
}

/// <summary>Ce qu'il faut pour dessiner une entité : sa forme, ses dimensions (mètres) et sa couleur.</summary>
public struct Renderable
{
    public MeshKind Mesh;

    /// <summary>Maillage du jeu (numéro dans la <see cref="MeshLibrary"/> du monde), prioritaire sur <see cref="Mesh"/> ; 0 pour aucun.</summary>
    public int Asset;

    public float3 Size;
    public float4 Color;

    /// <summary>Rayon de la sphère englobante (sélection au clic, cadrage).</summary>
    public readonly float BoundingRadius => Size.Length() * 0.5f;
}
