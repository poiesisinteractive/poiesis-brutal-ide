using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>Définition d'une classe de vaisseau, éditée dans <c>content/ships/*.ship.json</c>.</summary>
public sealed record ShipClass
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public float LengthMeters { get; init; }
    public float Hull { get; init; }
    public float CruiseSpeed { get; init; }
    public float WeaponRange { get; init; }
    public float WeaponDps { get; init; }
}

/// <summary>Données statiques chargées au démarrage du module, partagées par tous les mondes.</summary>
public sealed class ShipCatalog
{
    readonly List<ShipClass> _classes = new();
    readonly Dictionary<string, int> _byId = new(StringComparer.Ordinal);

    public IReadOnlyList<ShipClass> Classes => _classes;

    public int IndexOf(string id) =>
        _byId.TryGetValue(id, out var i) ? i : throw new KeyNotFoundException($"Classe de vaisseau inconnue : {id}");

    public void Add(ShipClass c)
    {
        _byId[c.Id] = _classes.Count;
        _classes.Add(c);
    }

    public static ShipCatalog Load(string shipsDirectory)
    {
        var catalog = new ShipCatalog();
        if (Directory.Exists(shipsDirectory))
            foreach (var file in Directory.EnumerateFiles(shipsDirectory, "*.ship.json").Order(StringComparer.Ordinal))
                catalog.Add(ValueCodec.Read<ShipClass>(File.ReadAllText(file)));
        return catalog;
    }
}
