using System.ComponentModel;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>Un scénario de bataille : sa graine et ses règles de croisière. Ses enfants sont les flottes et les astres.</summary>
[NodeType("orionum.scenario")]
public sealed class ScenarioNode : Node
{
    [Description("Graine de l'aléa : même graine, même bataille.")]
    public int Seed { get; set; } = 1;

    [Range(5, 500, 1)]
    [Description("Distance à l'ennemi (km) en deçà de laquelle les flottes quittent la croisière.")]
    public float CruiseCutoffKm { get; set; } = 35;

    [Range(100, 20000, 10)]
    [Description("Vitesse de croisière (m/s).")]
    public float CruiseSpeed { get; set; } = 3000;

    public override void Bake(BakeContext ctx)
    {
        var battle = ctx.World.Resource<Battle>();
        battle.Seed = Seed;
        battle.CruiseCutoff = CruiseCutoffKm * 1000;
        battle.CruiseSpeed = CruiseSpeed;
    }
}

/// <summary>
/// Une flotte placée dans l'éditeur : une composition (fichier <c>fleets/*.fleet.json</c>) multipliée par une échelle,
/// rangée en blocs par classe (amiral au centre, cuirassés et croiseurs en ligne, porteurs derrière, frégates sur les ailes).
/// </summary>
[NodeType("orionum.fleet")]
public sealed class FleetNode : Node
{
    public Faction Faction { get; set; }

    public float3 Position { get; set; }

    /// <summary>Cap initial, en degrés autour de la verticale (0 = vers +Z).</summary>
    [Range(-360, 360, 1)]
    public float Heading { get; set; }

    [ContentRef("fleets", "*.fleet.json")]
    public string Composition { get; set; } = "";

    [Range(1, 40, 0.1f)]
    [Description("Multiplie chaque effectif de la composition : ×20 donne 500 coques pour la flotte standard.")]
    public int Scale { get; set; } = 1;

    [Range(0.5f, 10, 0.05f)]
    [ScaleHandle]
    [Description("Espacement entre coques, en rayons de coque.")]
    public float Spacing { get; set; } = 3;

    public override void Bake(BakeContext ctx)
    {
        var world = ctx.World;
        var catalog = world.Resource<Catalog>();
        var battle = world.Resource<Battle>();
        var comp = catalog.Fleet(Composition);

        int group = battle.Groups.Length;
        battle.Groups = [.. battle.Groups, new Group { Faction = Faction, Centroid = Position, TargetGroup = -1 }];

        float yaw = Heading * MathF.PI / 180;
        var fwd = new float3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var frame = Frame.FromForward(fwd);

        // Une rangée par classe, de la plus grosse (au centre, devant) à la plus petite ; les porteurs derrière.
        var rows = comp.Ships
            .Select(entry => (Class: catalog.IndexOf(entry.Class), Count: entry.Count * Math.Max(1, Scale)))
            .OrderByDescending(r => catalog.Classes[r.Class].Def.Role == Role.Carrier ? -1 : catalog.Classes[r.Class].Def.Radius)
            .ToList();
        float depth = 0;
        foreach (var (classIndex, count) in rows)
        {
            var cls = catalog.Classes[classIndex].Def;
            float pitch = cls.Radius * 2 * Spacing;
            int perRow = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(count * 4f)));
            for (int i = 0; i < count; i++)
            {
                int row = i / perRow, col = i % perRow;
                int inRow = Math.Min(perRow, count - row * perRow);
                float x = (col - (inRow - 1) * 0.5f) * pitch;
                float z = -(depth + row * pitch);
                float y = (row % 2 == 0 ? 1 : -1) * cls.Radius * 0.5f * (row > 0 ? 1 : 0);
                var pos = Position + frame.Port * x + frame.Up * y + frame.Fwd * z;
                SpawnShip(ctx, catalog, classIndex, group, pos, fwd);
            }
            depth += (MathF.Ceiling(count / (float)perRow)) * pitch + cls.Radius;
        }
    }

    void SpawnShip(BakeContext ctx, Catalog catalog, int classIndex, int group, float3 pos, float3 fwd)
    {
        var world = ctx.World;
        var cls = catalog.Classes[classIndex];
        var def = cls.Def;
        var e = ctx.CreateEntity(this);
        world.Add(e, new Transform { Position = pos, Rotation = Frame.Rotation(fwd), Scale = 1 });
        world.Add(e, new Ship { Class = classIndex, Faction = Faction, Group = group, Hull = def.Hull });
        world.Add(e, new Motion { Forward = fwd });
        var shields = new Shields();
        for (int f = 0; f < 6; f++)
            shields[(Face)f] = def.ShieldFace;
        world.Add(e, shields);
        world.Add(e, new Subsystems { Engines = def.SubsystemHp, Weapons = def.SubsystemHp, ShieldGenerators = def.SubsystemHp });
        world.Add(e, new Threat());
        var bank = new WeaponBank();
        for (int w = 0; w < cls.Weapons.Length; w++)
        {
            int n = cls.Weapons[w].Hardpoints.Length;
            bank[w].Alive = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
            // Recharges décalées : toutes les bordées ne partent pas au même instant.
            bank[w].Cooldown = cls.Weapons[w].Cooldown * Rng.Float(world.Resource<Battle>().Seed, 0, e.Index, w);
        }
        world.Add(e, bank);
        world.Add(e, new Renderable
        {
            Mesh = def.Mesh,
            Asset = cls.MeshAsset,
            Size = cls.RenderSize,
            Color = Visuals.FactionHull(Faction),
        });
    }
}

/// <summary>Un astre de décor (étoile, planète, lune) : une sphère, sans gravité ni collision pour l'instant.</summary>
[NodeType("orionum.body")]
public sealed class BodyNode : Node
{
    public float3 Position { get; set; }

    [Range(100, 2_000_000, 100)]
    [ScaleHandle]
    [Description("Rayon (m).")]
    public float Radius { get; set; } = 20_000;

    public float3 Color { get; set; } = new(0.6f, 0.5f, 0.45f);

    public override void Bake(BakeContext ctx)
    {
        var e = ctx.CreateEntity(this);
        ctx.World.Add(e, new Transform { Position = Position, Rotation = floatQuat.Identity, Scale = 1 });
        ctx.World.Add(e, new Renderable { Mesh = MeshKind.Sphere, Size = new float3(Radius * 2), Color = new float4(Color, 1) });
    }
}
