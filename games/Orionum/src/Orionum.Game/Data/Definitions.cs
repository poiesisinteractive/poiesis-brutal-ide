using Brutal.Numerics;
using Poiesis.Engine;

namespace Orionum.Game;

public enum Faction { Coalition, Clans }

public enum Role { Frigate, Cruiser, Battleship, Flagship, Carrier }

/// <summary>Comportement d'engagement d'une classe (repris du POC).</summary>
public enum EngageStyle
{
    /// <summary>Tenir la distance, nez pointé : canon axial.</summary>
    Nose,
    /// <summary>Se mettre de flanc et tourner autour à distance : bordées.</summary>
    Broadside,
    /// <summary>Orbiter de près : frégates.</summary>
    Orbit,
    /// <summary>Rester loin, nez vers l'ennemi : porteurs.</summary>
    Standoff,
}

public enum WeaponKind { Kinetic, Laser, Missile, PointDefence }

/// <summary>Orientation d'une batterie : tourelle (tous azimuts dans son arc), flanc, ou axe du vaisseau.</summary>
public enum Mount { Turret, Port, Starboard, Nose }

/// <summary>
/// Emplacements de tourelles, en mètres dans le repère de la coque (+Z nez, +X bâbord, +Y dorsal).
/// Un seul des trois champs est renseigné dans le JSON.
/// </summary>
public sealed record HardpointSpec
{
    /// <summary><c>[n, x, y, z0, z1]</c> : n tourelles alignées de z0 à z1.</summary>
    public float[]? Row { get; init; }

    /// <summary><c>[n, demi-largeur, demi-hauteur, demi-longueur]</c> : réparties en alternance bâbord, tribord, dorsal, ventral.</summary>
    public float[]? Scatter { get; init; }

    public float3? Point { get; init; }

    public IEnumerable<float3> Expand()
    {
        if (Point is { } p)
        {
            yield return p;
        }
        else if (Row is [var n, var x, var y, var z0, var z1])
        {
            for (int i = 0; i < (int)n; i++)
                yield return new float3(x, y, n == 1 ? (z0 + z1) / 2 : z0 + (z1 - z0) * i / (n - 1));
        }
        else if (Scatter is [var sn, var hw, var hh, var hl])
        {
            for (int i = 0; i < (int)sn; i++)
            {
                float z = -hl + 2 * hl * (i + 0.5f) / sn;
                yield return (i % 4) switch
                {
                    0 => new float3(hw, 0, z),
                    1 => new float3(-hw, 0, z),
                    2 => new float3(0, hh, z),
                    _ => new float3(0, -hh, z),
                };
            }
        }
    }
}

/// <summary>Une batterie : une arme à plusieurs tourelles, détruites une à une.</summary>
public sealed record WeaponDef
{
    public string Name { get; init; } = "";
    public WeaponKind Kind { get; init; }
    public Mount Mount { get; init; }

    /// <summary>Rayon instantané (canon axial à particules) plutôt qu'un projectile.</summary>
    public bool Beam { get; init; }

    /// <summary>Lance : durée du rayon continu (s) ; <see cref="Damage"/> est alors le total d'une passe.</summary>
    public float BeamDuration { get; init; }

    public float Damage { get; init; }

    /// <summary>Cadence : tirs par seconde de la batterie.</summary>
    public float Rof { get; init; }

    public float Range { get; init; }
    public float Speed { get; init; }

    /// <summary>Demi-angle de l'arc de tir (degrés) ; 180 = tous azimuts.</summary>
    public float ArcDeg { get; init; } = 180;

    /// <summary>Dispersion de base (radians).</summary>
    public float Spread { get; init; }

    /// <summary>Vitesse angulaire de cible au-delà de laquelle la dispersion augmente (rad/s) ; 0 = sans limite.</summary>
    public float Tracking { get; init; }

    public float VsShield { get; init; } = 1;
    public float VsHull { get; init; } = 1;

    /// <summary>Part des dégâts qui traverse les boucliers.</summary>
    public float Pierce { get; init; }

    /// <summary>Missiles par salve.</summary>
    public int Salvo { get; init; } = 1;

    /// <summary>Virage des missiles (rad/s).</summary>
    public float Turn { get; init; }

    /// <summary>Points de vie d'une tourelle (dégâts localisés).</summary>
    public float TurretHp { get; init; } = 1000;

    /// <summary>Défense ponctuelle : chance de toucher un missile par tir.</summary>
    public float HitChance { get; init; } = 0.3f;

    public List<HardpointSpec> Hardpoints { get; init; } = new();
}

/// <summary>Une classe de vaisseau, éditée dans <c>content/ships/*.ship.json</c>. Mètres et secondes.</summary>
public sealed record ShipClass
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Faction Faction { get; init; }
    public Role Role { get; init; }
    public MeshKind Mesh { get; init; } = MeshKind.Hull;

    public float LengthMeters { get; init; } = 100;
    public float Radius { get; init; } = 50;

    public float Hull { get; init; }
    public float ShieldFace { get; init; }
    public float ShieldRegen { get; init; }
    public float SubsystemHp { get; init; }

    public float Speed { get; init; }
    public float Accel { get; init; }
    public float TurnRateDeg { get; init; }
    /// <summary>Poussée des propulseurs de manœuvre, en fraction du moteur principal.</summary>
    public float Rcs { get; init; }
    /// <summary>Accélération angulaire (rad/s²).</summary>
    public float AngAccel { get; init; }

    public EngageStyle Engage { get; init; }
    public float PreferredRange { get; init; }

    public List<WeaponDef> Weapons { get; init; } = new();
}

/// <summary>Une composition de flotte, éditée dans <c>content/fleets/*.fleet.json</c>.</summary>
public sealed record FleetComposition
{
    public string Id { get; init; } = "";
    public List<FleetEntry> Ships { get; init; } = new();
}

public sealed record FleetEntry
{
    public string Class { get; init; } = "";
    public int Count { get; init; } = 1;
}
