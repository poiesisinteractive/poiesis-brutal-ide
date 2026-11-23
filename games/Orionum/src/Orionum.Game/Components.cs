using Brutal.Numerics;

namespace Orionum.Game;

public enum Faction { Coalition, Clans }

public struct Velocity
{
    public float3 Value;
}

/// <summary>Coque d'un vaisseau : sa classe (index dans <see cref="ShipCatalog"/>), son camp, sa coque restante.</summary>
public struct Hull
{
    public int ClassIndex;
    public Faction Faction;
    public float Health;
}
