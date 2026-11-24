using Brutal.Numerics;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>Une arme prête pour la sim : sa définition et ses tourelles dépliées.</summary>
public sealed class CompiledWeapon(WeaponDef def)
{
    public WeaponDef Def { get; } = def;
    public float3[] Hardpoints { get; } = def.Hardpoints.SelectMany(h => h.Expand()).Take(64).ToArray();
    public float ArcCos { get; } = MathF.Cos(def.ArcDeg * MathF.PI / 180);
    public float Cooldown { get; } = def.Rof > 0 ? 1 / def.Rof : float.PositiveInfinity;
}

/// <summary>Une classe prête pour la sim.</summary>
public sealed class CompiledClass
{
    public const int MaxWeapons = 8;

    public CompiledClass(ShipClass def)
    {
        Def = def;
        if (def.Weapons.Count > MaxWeapons)
            throw new InvalidDataException($"{def.Id} : au plus {MaxWeapons} batteries");
        Weapons = def.Weapons.Select(w => new CompiledWeapon(w)).ToArray();
        TurnRate = def.TurnRateDeg * MathF.PI / 180;
        MaxRange = Weapons.Where(w => w.Def.Kind != WeaponKind.PointDefence).Select(w => w.Def.Range).DefaultIfEmpty(0).Max();
    }

    public ShipClass Def { get; }
    public CompiledWeapon[] Weapons { get; }
    public float TurnRate { get; }
    public float MaxRange { get; }
}

/// <summary>Données statiques chargées avec le module : classes de vaisseaux et compositions de flottes.</summary>
public sealed class Catalog
{
    readonly List<CompiledClass> _classes = new();
    readonly Dictionary<string, int> _classIndex = new(StringComparer.Ordinal);
    readonly Dictionary<string, FleetComposition> _fleets = new(StringComparer.Ordinal);

    public IReadOnlyList<CompiledClass> Classes => _classes;

    public float MaxShipRadius { get; private set; }

    public int IndexOf(string classId) =>
        _classIndex.TryGetValue(classId, out var i) ? i : throw new KeyNotFoundException($"Classe de vaisseau inconnue : {classId}");

    public FleetComposition Fleet(string id) =>
        _fleets.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Composition de flotte inconnue : {id}");

    public void Add(ShipClass c)
    {
        _classIndex[c.Id] = _classes.Count;
        _classes.Add(new CompiledClass(c));
        MaxShipRadius = Math.Max(MaxShipRadius, c.Radius);
    }

    public void Add(FleetComposition f) => _fleets[f.Id] = f;

    public static Catalog Load(string contentRoot)
    {
        var catalog = new Catalog();
        foreach (var file in Files(Path.Combine(contentRoot, "ships"), "*.ship.json"))
            catalog.Add(Read<ShipClass>(file));
        foreach (var file in Files(Path.Combine(contentRoot, "fleets"), "*.fleet.json"))
            catalog.Add(Read<FleetComposition>(file));
        return catalog;
    }

    static T Read<T>(string file)
    {
        try
        {
            return ValueCodec.Read<T>(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(file)} : {ex.Message}", ex);
        }
    }

    static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Order(StringComparer.Ordinal) : [];
}
