using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>Un scénario de bataille : sa graine et sa durée. Ses enfants sont les flottes.</summary>
[NodeType("orionum.scenario")]
public sealed class ScenarioNode : Node
{
    public int Seed { get; set; } = 1;

    [Range(10, 7200, 5)]
    public float DurationSeconds { get; set; } = 600;
}

/// <summary>Une flotte placée dans l'éditeur. Au Play, elle génère ses coques en ligne de bataille.</summary>
[NodeType("orionum.fleet")]
public sealed class FleetNode : Node
{
    public Faction Faction { get; set; }

    public float3 Position { get; set; }

    /// <summary>Identifiant de classe dans le catalogue (ex. <c>"coalition.cruiser"</c>).</summary>
    [ContentRef("ships", "*.ship.json")]
    public string ShipClass { get; set; } = "";

    [Range(1, 500, 0.2f)]
    public int Count { get; set; } = 4;

    /// <summary>Écart entre deux coques de la ligne, en mètres.</summary>
    [Range(50, 20000, 10)]
    public float Spacing { get; set; } = 1500;

    public override void Bake(BakeContext ctx)
    {
        var catalog = ctx.World.Resource<ShipCatalog>();
        int classIndex = catalog.IndexOf(ShipClass);
        var ship = catalog.Classes[classIndex];

        for (int i = 0; i < Count; i++)
        {
            var e = ctx.CreateEntity(this);
            var offset = new float3((i - (Count - 1) * 0.5f) * Spacing, 0, 0);
            var t = Transform.Identity;
            t.Position = Position + offset;
            // Proue vers l'ennemi, supposé de l'autre côté de l'origine.
            if (Position.Z > 0)
                t.Rotation = floatQuat.CreateFromYawPitchRoll(MathF.PI, 0, 0);
            ctx.World.Add(e, t);
            ctx.World.Add(e, new Velocity());
            ctx.World.Add(e, new Hull { ClassIndex = classIndex, Faction = Faction, Health = ship.Hull });
            ctx.World.Add(e, new Renderable
            {
                Mesh = Faction == Faction.Coalition ? MeshKind.Hull : MeshKind.HeavyHull,
                Size = new float3(ship.LengthMeters * 0.22f, ship.LengthMeters * 0.16f, ship.LengthMeters),
                Color = Faction == Faction.Coalition ? new float4(0.82f, 0.88f, 1f, 1f) : new float4(0.75f, 0.48f, 0.30f, 1f),
            });
        }
    }
}
