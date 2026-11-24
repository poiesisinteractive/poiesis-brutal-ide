using Brutal.Numerics;
using Poiesis.Engine;
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

    /// <summary>Maillage propre à la classe (coque générée ou modèle importé), numéro dans <see cref="Catalog.Meshes"/> ; 0 sinon.</summary>
    public int MeshAsset { get; internal set; }

    /// <summary>Taille de rendu d'une coque de la classe (mètres) : proportions du maillage, longueur de la classe.</summary>
    public float3 RenderSize { get; internal set; }
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

    /// <summary>Les maillages des classes (coques générées, modèles importés), installés dans chaque monde.</summary>
    public MeshLibrary Meshes { get; } = new();

    /// <summary>La texture de blindage des coques générées, partagée par toutes les classes.</summary>
    static TextureData? _plating;

    public static Catalog Load(string contentRoot)
    {
        var catalog = new Catalog();
        foreach (var file in Files(Path.Combine(contentRoot, "ships"), "*.ship.json"))
        {
            var def = Read<ShipClass>(file);
            catalog.Add(def);
            try
            {
                catalog.AttachMesh(catalog.Classes[^1], contentRoot);
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException($"{Path.GetFileName(file)} : maillage : {ex.Message}", ex);
            }
        }
        foreach (var file in Files(Path.Combine(contentRoot, "fleets"), "*.fleet.json"))
            catalog.Add(Read<FleetComposition>(file));
        return catalog;
    }

    /// <summary>
    /// Le maillage d'une classe : modèle glTF s'il y en a un, sinon coque générée, sinon forme de base (<see cref="ShipClass.Mesh"/>).
    /// </summary>
    void AttachMesh(CompiledClass cls, string contentRoot)
    {
        var (asset, size) = ShipMesh(cls.Def, contentRoot);
        cls.RenderSize = size;
        if (asset is not null)
            cls.MeshAsset = Meshes.Add(asset);
    }

    /// <summary>
    /// Le maillage d'une classe et sa taille de rendu (mètres). Modèle glTF (mis à la longueur de la classe), coque
    /// générée (déjà en mètres), ou forme de base (pas de maillage propre). L'origine du vaisseau reste celle du
    /// maillage : les tourelles, placées par rapport à elle, tombent sur la coque.
    /// </summary>
    public static (MeshAsset? Asset, float3 Size) ShipMesh(ShipClass def, string contentRoot)
    {
        if (def.Model is { Length: > 0 } model)
        {
            var imported = Poiesis.Engine.Assets.ModelImporter.Load(Path.Combine(contentRoot, model));
            var (min, max) = imported.Mesh.Bounds();
            float span = MathF.Max(max.Z - min.Z, 1e-3f);
            var size = imported.Mesh.NormalizeToUnitBox(keepOrigin: true);
            return (new MeshAsset("model:" + model, imported.Mesh, imported.BaseColor), size * (def.LengthMeters / span));
        }
        if (def.Design is { } design)
        {
            var mesh = HullGenerator.Build(design, def.LengthMeters).Mesh;
            var size = mesh.NormalizeToUnitBox(keepOrigin: true);
            return (new MeshAsset("hull:" + def.Id, mesh, _plating ??= HullGenerator.Plating()), size);
        }
        return (null, new float3(def.LengthMeters * 0.24f, def.LengthMeters * 0.16f, def.LengthMeters));
    }

    /// <summary>La texture de blindage partagée par les coques générées.</summary>
    public static TextureData Plating => _plating ??= HullGenerator.Plating();

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
