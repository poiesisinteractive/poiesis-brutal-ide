using Brutal.Numerics;

namespace Poiesis.Engine;

/// <summary>
/// Un maillage en mémoire : positions, normales, coordonnées de texture (facultatives) et indices.
/// Fabriqué par le jeu (générateur de coques), par l'import d'un modèle (glTF) ou par les formes de l'éditeur.
/// </summary>
public sealed class MeshData
{
    public List<float3> Positions { get; } = new();
    public List<float3> Normals { get; } = new();

    /// <summary>Coordonnées de texture, une par sommet ; vide si le maillage n'est pas texturé.</summary>
    public List<float2> TexCoords { get; } = new();

    public List<uint> Indices { get; } = new();

    public int TriangleCount => Indices.Count / 3;

    /// <summary>Ajoute un triangle à facette plate (normale calculée).</summary>
    public void Triangle(float3 a, float3 b, float3 c)
    {
        var n = float3.NormalizeOrZero(float3.Cross(b - a, c - a));
        uint i = (uint)Positions.Count;
        Positions.Add(a); Positions.Add(b); Positions.Add(c);
        Normals.Add(n); Normals.Add(n); Normals.Add(n);
        Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
    }

    public void Quad(float3 a, float3 b, float3 c, float3 d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    /// <summary>Un prisme entre deux sections (4 coins chacune, dans le même ordre), extrémités fermées.</summary>
    public void Loft(ReadOnlySpan<float3> back, ReadOnlySpan<float3> front)
    {
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            Quad(back[i], back[j], front[j], front[i]);
        }
        Quad(back[3], back[2], back[1], back[0]);
        Quad(front[0], front[1], front[2], front[3]);
    }

    /// <summary>Boîte englobante des positions.</summary>
    public (float3 Min, float3 Max) Bounds()
    {
        if (Positions.Count == 0)
            return (float3.Zero, float3.Zero);
        var min = Positions[0];
        var max = Positions[0];
        foreach (var p in Positions)
        {
            min = float3.Min(min, p);
            max = float3.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>
    /// Ramène le maillage dans le cube unité centré [-0,5 ; 0,5]³ (chaque axe à sa propre échelle), comme les formes de
    /// l'éditeur : le rendu le met ensuite à la taille voulue (<see cref="Renderable.Size"/>). Rend la taille d'origine.
    /// Avec <paramref name="keepOrigin"/>, l'origine du maillage reste au centre du cube (la boîte devient symétrique) :
    /// ce qui est placé par rapport à l'origine (tourelles d'un vaisseau) reste à sa place.
    /// </summary>
    public float3 NormalizeToUnitBox(bool keepOrigin = false)
    {
        var (min, max) = Bounds();
        if (keepOrigin)
        {
            max = float3.Max(float3.Abs(min), float3.Abs(max));
            min = -max;
        }
        var size = float3.Max(max - min, new float3(1e-6f));
        var center = (min + max) * 0.5f;
        for (int i = 0; i < Positions.Count; i++)
            Positions[i] = (Positions[i] - center) / size;
        // Normales d'une mise à l'échelle non uniforme : multipliées par l'échelle inverse transposée, soit ici la taille.
        for (int i = 0; i < Normals.Count; i++)
            Normals[i] = float3.NormalizeOrZero(Normals[i] * size);
        return size;
    }
}

/// <summary>Une image RGBA 8 bits (couleur de base d'un modèle).</summary>
public sealed record TextureData(int Width, int Height, byte[] Rgba);

/// <summary>Un maillage désigné par une clé stable (chemin du modèle, classe de vaisseau…), avec sa texture éventuelle.</summary>
public sealed record MeshAsset(string Key, MeshData Mesh, TextureData? Texture = null);

/// <summary>
/// Les maillages propres au jeu, en ressource du monde : un <see cref="Renderable"/> en désigne un par
/// <see cref="Renderable.Asset"/> (1 pour le premier, 0 pour aucun). Le rendu les téléverse à la première utilisation.
/// </summary>
public sealed class MeshLibrary
{
    readonly List<MeshAsset> _assets = new();
    readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);

    public IReadOnlyList<MeshAsset> Assets => _assets;

    /// <summary>Ajoute (ou remplace, à clé égale) un maillage ; rend son numéro pour <see cref="Renderable.Asset"/>.</summary>
    public int Add(MeshAsset asset)
    {
        if (_byKey.TryGetValue(asset.Key, out var id))
        {
            _assets[id - 1] = asset;
            return id;
        }
        _assets.Add(asset);
        return _byKey[asset.Key] = _assets.Count;
    }

    public MeshAsset? Get(int id) => id >= 1 && id <= _assets.Count ? _assets[id - 1] : null;

    public int IdOf(string key) => _byKey.GetValueOrDefault(key);
}
