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
