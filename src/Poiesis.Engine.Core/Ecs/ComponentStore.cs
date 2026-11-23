namespace Poiesis.Engine.Ecs;

/// <summary>Partie non générique d'un stockage de composants, pour que le monde puisse les manipuler en bloc.</summary>
public interface IComponentStore
{
    Type ComponentType { get; }
    int Count { get; }
    bool Has(int entityIndex);
    void Remove(int entityIndex);
    IComponentStore Clone();
}

/// <summary>
/// Stockage dense (sparse set) d'un type de composant. Les composants sont des structs contigus,
/// accessibles par <c>ref</c>. Le monde entier se clone par copie de tableaux, sans GC,
/// ce qui sert aux projections de sim et au snapshot du Play mode.
/// </summary>
/// <remarks>
/// L'ordre d'itération est celui de <see cref="DenseEntities"/>. Pour garder la sim déterministe,
/// <see cref="Remove"/> préserve l'ordre (décalage) au lieu de permuter avec le dernier.
/// À profiler : si le coût devient visible, passer à un compactage différé en fin de pas.
/// </remarks>
public sealed class ComponentStore<T> : IComponentStore
    where T : struct
{
    int[] _sparse;   // index d'entité -> position dense + 1 (0 = absent)
    int[] _dense;    // position dense -> index d'entité
    T[] _items;

    public ComponentStore(int capacity = 64)
    {
        capacity = Math.Max(capacity, 1);
        _sparse = new int[capacity];
        _dense = new int[capacity];
        _items = new T[capacity];
    }

    public Type ComponentType => typeof(T);

    public int Count { get; private set; }

    public ReadOnlySpan<int> DenseEntities => _dense.AsSpan(0, Count);

    public Span<T> Components => _items.AsSpan(0, Count);

    public bool Has(int entityIndex) => (uint)entityIndex < (uint)_sparse.Length && _sparse[entityIndex] != 0;

    public ref T Get(int entityIndex)
    {
        if (!Has(entityIndex))
            throw new KeyNotFoundException($"L'entité {entityIndex} n'a pas de composant {typeof(T).Name}");
        return ref _items[_sparse[entityIndex] - 1];
    }

    public ref T Set(int entityIndex, in T value)
    {
        if (Has(entityIndex))
        {
            ref var existing = ref _items[_sparse[entityIndex] - 1];
            existing = value;
            return ref existing;
        }

        if (entityIndex >= _sparse.Length)
            Array.Resize(ref _sparse, Math.Max(entityIndex + 1, _sparse.Length * 2));
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
            Array.Resize(ref _dense, _dense.Length * 2);
        }

        // Insertion triée par index d'entité : l'itération suit l'ordre de création des slots.
        int pos = Count;
        while (pos > 0 && _dense[pos - 1] > entityIndex)
        {
            _dense[pos] = _dense[pos - 1];
            _items[pos] = _items[pos - 1];
            _sparse[_dense[pos]] = pos + 1;
            pos--;
        }

        _dense[pos] = entityIndex;
        _items[pos] = value;
        _sparse[entityIndex] = pos + 1;
        Count++;
        return ref _items[pos];
    }

    public void Remove(int entityIndex)
    {
        if (!Has(entityIndex))
            return;

        int pos = _sparse[entityIndex] - 1;
        _sparse[entityIndex] = 0;
        Count--;
        for (int i = pos; i < Count; i++)
        {
            _dense[i] = _dense[i + 1];
            _items[i] = _items[i + 1];
            _sparse[_dense[i]] = i + 1;
        }
        _items[Count] = default;
    }

    public IComponentStore Clone()
    {
        var copy = new ComponentStore<T>(_items.Length)
        {
            _sparse = (int[])_sparse.Clone(),
            _dense = (int[])_dense.Clone(),
            _items = (T[])_items.Clone(),
            Count = Count,
        };
        return copy;
    }
}
