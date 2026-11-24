using Brutal.Numerics;

namespace Poiesis.Engine.Spatial;

/// <summary>
/// Grille uniforme de hachage pour les requêtes de voisinage (ciblage, impacts, défense ponctuelle).
/// Reconstruite d'un bloc à chaque pas : on y range des indices (slot d'entité, index de table…) et leur position.
/// </summary>
/// <remarks>
/// Les candidats rendus par une requête ne sont qu'un sur-ensemble : l'appelant vérifie la distance.
/// Dans une cellule, les éléments sont triés par indice ; l'ordre des cellules est fixe. Une requête rend donc
/// toujours les mêmes candidats dans le même ordre (déterminisme), quel que soit l'ordre d'insertion.
/// Les requêtes sont sûres en parallèle une fois la grille construite.
/// </remarks>
public sealed class SpatialHash(float cellSize)
{
    const int IndexBits = 22;   // jusqu'à 4 millions d'éléments
    const long IndexMask = (1L << IndexBits) - 1;

    struct Entry : IComparable<Entry>
    {
        public long Key;     // cellule << IndexBits | indice : trier par clé, c'est trier par cellule puis par indice
        public float3 Position;

        public readonly int CompareTo(Entry other) => Key.CompareTo(other.Key);
    }

    Entry[] _entries = new Entry[256];
    readonly Dictionary<long, (int Start, int Count)> _cells = new();
    int _count;

    public float CellSize { get; } = cellSize;

    public int Count => _count;

    /// <summary>Remplit la grille : <paramref name="positions"/>[i] est la position de l'élément <paramref name="indices"/>[i].</summary>
    public void Build(ReadOnlySpan<int> indices, ReadOnlySpan<float3> positions)
    {
        _count = indices.Length;
        if (_entries.Length < _count)
            Array.Resize(ref _entries, Math.Max(_count, _entries.Length * 2));
        for (int i = 0; i < _count; i++)
            _entries[i] = new Entry { Key = (CellKey(Cell(positions[i])) << IndexBits) | (indices[i] & IndexMask), Position = positions[i] };
        Array.Sort(_entries, 0, _count);

        _cells.Clear();
        for (int i = 0; i < _count;)
        {
            long cell = _entries[i].Key >> IndexBits;
            int start = i;
            while (i < _count && _entries[i].Key >> IndexBits == cell)
                i++;
            _cells[cell] = (start, i - start);
        }
    }

    /// <summary>
    /// Appelle <paramref name="visit"/> pour chaque élément à moins de <paramref name="radius"/> de
    /// <paramref name="center"/> (distance exacte vérifiée), avec son indice et le carré de sa distance.
    /// </summary>
    public void Query<TState>(float3 center, float radius, ref TState state, QueryVisitor<TState> visit)
    {
        var min = Cell(center - new float3(radius));
        var max = Cell(center + new float3(radius));
        float r2 = radius * radius;
        for (long x = min.X; x <= max.X; x++)
            for (long y = min.Y; y <= max.Y; y++)
                for (long z = min.Z; z <= max.Z; z++)
                {
                    if (!_cells.TryGetValue(CellKey((x, y, z)), out var c))
                        continue;
                    for (int i = c.Start; i < c.Start + c.Count; i++)
                    {
                        var d = _entries[i].Position - center;
                        float d2 = float3.Dot(d, d);
                        if (d2 <= r2)
                            visit(ref state, (int)(_entries[i].Key & IndexMask), d2);
                    }
                }
    }

    public delegate void QueryVisitor<TState>(ref TState state, int index, float distanceSquared);

    (long X, long Y, long Z) Cell(float3 p) =>
        ((long)MathF.Floor(p.X / CellSize), (long)MathF.Floor(p.Y / CellSize), (long)MathF.Floor(p.Z / CellSize));

    /// <summary>Clé de cellule sur 41 bits (coordonnées repliées sur ±2^13 cellules par axe) ; une collision ne fait qu'ajouter des candidats.</summary>
    static long CellKey((long X, long Y, long Z) c) =>
        ((c.X & 0x1FFF) << 26) | ((c.Y & 0x1FFF) << 13) | (c.Z & 0x1FFF);
}
