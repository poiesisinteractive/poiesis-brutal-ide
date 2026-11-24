using Brutal.Numerics;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Spatial;

namespace Orionum.Game;

/// <summary>Un projectile : obus, trait laser ou missile. Rangé dans une table de la bataille, pas dans l'ECS.</summary>
public struct Projectile
{
    public float3 Position;
    public float3 Velocity;
    public float Damage;
    public float VsShield;
    public float VsHull;
    public float Pierce;
    public float Life;
    public Entity Shooter;
    public Faction Faction;
    public WeaponKind Kind;

    /// <summary>Missile : cible suivie, virage (rad/s), vitesse de croisière, points de vie (défense ponctuelle).</summary>
    public Entity Target;
    public float Turn;
    public float Speed;
    public float Hp;

    public bool Alive;
}

/// <summary>Un rayon visible (lance, canon à particules, défense ponctuelle), quelques dixièmes de seconde.</summary>
public struct BeamFx
{
    public float3 From;
    public float3 To;
    public Faction Faction;
    public float Life;
    public float MaxLife;
    public float Width;
}

/// <summary>Une explosion : la mort d'une coque ou la rupture d'une tourelle.</summary>
public struct ExplosionFx
{
    public float3 Position;
    public float Radius;
    public float Life;
    public float MaxLife;
}

/// <summary>Une escadre : une flotte de la scène.</summary>
public struct Group
{
    public Faction Faction;
    public float3 Centroid;
    public int Alive;
    public int TargetGroup;
}

/// <summary>
/// L'état de la bataille hors ECS : projectiles, effets, escadres, issue. Copié par les instantanés
/// (<see cref="ICloneableResource"/>). Les grilles spatiales, reconstruites à chaque pas, ne sont pas copiées.
/// </summary>
public sealed class Battle : ICloneableResource
{
    public int Seed { get; set; }

    /// <summary>Distance à l'ennemi en deçà de laquelle la croisière se coupe (m).</summary>
    public float CruiseCutoff { get; set; } = 35_000;

    /// <summary>Vitesse de croisière (m/s).</summary>
    public float CruiseSpeed { get; set; } = 3000;

    public double Time;
    public Faction? Winner;
    public double EndTime;

    public Projectile[] Projectiles = new Projectile[1024];
    public int ProjectileCount;

    public List<BeamFx> Beams = new();
    public List<ExplosionFx> Explosions = new();
    public Group[] Groups = [];

    public int ShotsFired;
    public int MissilesIntercepted;
    public int TurretsDestroyed;
    public int[] Losses = new int[2];

    // --- Grilles (reconstruites à chaque pas, jamais copiées) ---------------------------------------
    /// <summary>Grille large des coques : ciblage à longue portée.</summary>
    [NonCloned] public SpatialHash ShipsWide = new(10_000);
    /// <summary>Grille fine des coques : impacts.</summary>
    [NonCloned] public SpatialHash ShipsNear = new(2_500);
    /// <summary>
    /// Photo des positions et vitesses des coques en début de pas, indexée par slot d'entité. Les phases
    /// parallèles lisent les autres coques ici, jamais dans les composants qu'un autre thread est en train d'écrire.
    /// </summary>
    [NonCloned] public float3[] SlotPosition = new float3[256];
    [NonCloned] public float3[] SlotVelocity = new float3[256];

    /// <summary>Grille des missiles : défense ponctuelle.</summary>
    [NonCloned] public SpatialHash Missiles = new(2_000);

    public ref Projectile Spawn()
    {
        if (ProjectileCount == Projectiles.Length)
            Array.Resize(ref Projectiles, Projectiles.Length * 2);
        ref var p = ref Projectiles[ProjectileCount++];
        p = default;
        p.Alive = true;
        return ref p;
    }

    /// <summary>Retire les projectiles morts en gardant l'ordre des vivants (déterminisme).</summary>
    public void CompactProjectiles()
    {
        int w = 0;
        for (int r = 0; r < ProjectileCount; r++)
            if (Projectiles[r].Alive)
                Projectiles[w++] = Projectiles[r];
        ProjectileCount = w;
    }

    public object CloneResource()
    {
        var copy = (Battle)MemberwiseClone();
        copy.Projectiles = (Projectile[])Projectiles.Clone();
        copy.Beams = new List<BeamFx>(Beams);
        copy.Explosions = new List<ExplosionFx>(Explosions);
        copy.Groups = (Group[])Groups.Clone();
        copy.Losses = (int[])Losses.Clone();
        copy.ShipsWide = new SpatialHash(ShipsWide.CellSize);
        copy.ShipsNear = new SpatialHash(ShipsNear.CellSize);
        copy.Missiles = new SpatialHash(Missiles.CellSize);
        copy.SlotPosition = new float3[SlotPosition.Length];
        copy.SlotVelocity = new float3[SlotVelocity.Length];
        return copy;
    }
}

[AttributeUsage(AttributeTargets.Field)]
sealed class NonClonedAttribute : Attribute;

/// <summary>Aléa déterministe sans état : une valeur par (graine, pas, entité, sel). Sûr en parallèle.</summary>
public static class Rng
{
    public static ulong Hash(int seed, long tick, int a, int b)
    {
        ulong x = (ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)tick * 0xBF58476D1CE4E5B9UL ^ (ulong)(uint)a * 0x94D049BB133111EBUL ^ (ulong)(uint)b;
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }

    /// <summary>Flottant uniforme dans [0, 1).</summary>
    public static float Float(int seed, long tick, int a, int b) => (Hash(seed, tick, a, b) >> 40) / (float)(1UL << 24);

    /// <summary>Vecteur unitaire uniforme.</summary>
    public static float3 Unit(int seed, long tick, int a, int b)
    {
        float z = Float(seed, tick, a, b) * 2 - 1;
        float t = Float(seed, tick, a, b + 7919) * MathF.Tau;
        float r = MathF.Sqrt(MathF.Max(0, 1 - z * z));
        return new float3(r * MathF.Cos(t), r * MathF.Sin(t), z);
    }
}

/// <summary>Repère d'une coque et conversions.</summary>
public static class Frame
{
    public static readonly float3 WorldUp = new(0, 1, 0);

    /// <summary>Repère (bâbord, dorsal, nez) d'un cap ; le dorsal reste au plus près du haut du monde.</summary>
    public static (float3 Port, float3 Up, float3 Fwd) FromForward(float3 fwd)
    {
        var up = WorldUp;
        if (MathF.Abs(float3.Dot(up, fwd)) > 0.98f)
            up = new float3(1, 0, 0);
        var port = float3.Normalize(float3.Cross(up, fwd));
        up = float3.Cross(fwd, port);
        return (port, up, fwd);
    }

    /// <summary>Point du repère de la coque (+X bâbord, +Y dorsal, +Z nez) vers le monde.</summary>
    public static float3 ToWorld(float3 position, float3 fwd, float3 local)
    {
        var f = FromForward(fwd);
        return position + f.Port * local.X + f.Up * local.Y + f.Fwd * local.Z;
    }

    /// <summary>Quaternion de rendu qui envoie +Z sur le cap (convention lignes de Brutal.Numerics).</summary>
    public static floatQuat Rotation(float3 fwd)
    {
        var f = FromForward(fwd);
        var m = float4x4.Identity;
        m.X = new float4(f.Port, 0);
        m.Y = new float4(f.Up, 0);
        m.Z = new float4(f.Fwd, 0);
        return floatQuat.Normalize(floatQuat.CreateFromRotationMatrix(m));
    }

    /// <summary>Tourne <paramref name="v"/> autour de <paramref name="axis"/> (unitaire) de <paramref name="angle"/> radians (Rodrigues).</summary>
    public static float3 RotateAxis(float3 v, float3 axis, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return v * c + float3.Cross(axis, v) * s + axis * (float3.Dot(axis, v) * (1 - c));
    }

    public static float3 ClampLength(float3 v, float max)
    {
        float l = v.Length();
        return l > max && l > 0 ? v * (max / l) : v;
    }

    public static float3 SafeNormalize(float3 v, float3 fallback)
    {
        float l = v.Length();
        return l > 1e-6f ? v / l : fallback;
    }
}
