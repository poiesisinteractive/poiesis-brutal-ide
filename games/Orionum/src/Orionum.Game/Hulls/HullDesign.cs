namespace Orionum.Game;

/// <summary>La forme de la proue.</summary>
public enum BowStyle
{
    /// <summary>Effilée, en pointe.</summary>
    Pointed,
    /// <summary>En lame : large et plate.</summary>
    Wedge,
    /// <summary>Massive, à peine rétrécie, face avant blindée.</summary>
    Blunt,
    /// <summary>Fourchue : deux pointes de part et d'autre d'un creux.</summary>
    Split,
}

/// <summary>Un bloc de superstructure posé sur le pont (passerelle, tour de commandement), en étages.</summary>
public sealed record Superstructure
{
    /// <summary>Position le long de la coque : -0,5 à la poupe, 0,5 à la proue.</summary>
    public float At { get; init; }

    /// <summary>Longueur, largeur et hauteur, en fractions de la longueur du vaisseau.</summary>
    public float Length { get; init; } = 0.2f;
    public float Width { get; init; } = 0.08f;
    public float Height { get; init; } = 0.05f;

    /// <summary>Nombre d'étages, chacun plus étroit et plus court que le précédent.</summary>
    public int Tiers { get; init; } = 1;
}

/// <summary>
/// Les paramètres d'une coque générée (champ <c>design</c> d'un <c>*.ship.json</c>), sans unité : les dimensions sont
/// des fractions de la longueur du vaisseau, qui reste celle de sa classe (<c>lengthMeters</c>).
/// </summary>
public sealed record HullDesign
{
    public BowStyle Bow { get; init; } = BowStyle.Pointed;

    /// <summary>Longueur de la proue (fraction de la longueur).</summary>
    public float BowLength { get; init; } = 0.25f;

    /// <summary>Largeur et hauteur maximales du corps.</summary>
    public float Beam { get; init; } = 0.22f;
    public float Height { get; init; } = 0.12f;

    /// <summary>Nombre de segments du corps, et leur pincement aux jointures (0 : aucun, 0,5 : très marqué).</summary>
    public int Sections { get; init; } = 3;
    public float Waist { get; init; } = 0.12f;

    /// <summary>Rétrécissement de la poupe (0 : aucun, 0,6 : poupe très fine).</summary>
    public float SternTaper { get; init; } = 0.2f;

    /// <summary>Tuyères à la poupe : nombre et diamètre (fraction de la longueur).</summary>
    public int Engines { get; init; } = 2;
    public float EngineSize { get; init; } = 0.05f;

    /// <summary>Hauteur de la quille ventrale (0 : sans quille).</summary>
    public float Keel { get; init; }

    public List<Superstructure> Superstructures { get; init; } = new();
}
