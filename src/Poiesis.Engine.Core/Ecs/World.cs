namespace Poiesis.Engine.Ecs;

/// <summary>
/// Monde ECS : entités, stockages de composants et ressources globales.
/// C'est l'état d'exécution du jeu ; les nœuds de l'éditeur ne vivent pas ici (voir Poiesis.Engine.Scene).
/// </summary>
public sealed class World
{
    readonly Dictionary<Type, IComponentStore> _stores = new();
    readonly Dictionary<Type, object> _resources = new();
    int[] _generations = new int[64];
    bool[] _alive = new bool[64];
    readonly Stack<int> _freeSlots = new();
    int _nextSlot;

    /// <summary>Nombre de pas de sim exécutés depuis la création du monde.</summary>
    public long Tick { get; internal set; }

    public int EntityCount { get; private set; }

    public Entity Create()
    {
        // Réutilise le dernier slot libéré (pile) : mêmes opérations, mêmes slots, ordre déterministe.
        int index = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot++;
        if (index >= _generations.Length)
        {
            Array.Resize(ref _generations, _generations.Length * 2);
            Array.Resize(ref _alive, _generations.Length);
        }
        int generation = ++_generations[index];
        _alive[index] = true;
        EntityCount++;
        return new Entity(index, generation);
    }

    public bool IsAlive(Entity e) =>
        !e.IsNull && (uint)e.Index < (uint)_nextSlot && _generations[e.Index] == e.Generation;

    /// <summary>Entité vivante qui occupe un slot (les stockages de composants n'exposent que les slots).</summary>
    public Entity EntityAt(int index) =>
        (uint)index < (uint)_nextSlot && _alive[index] ? new Entity(index, _generations[index]) : Entity.Null;

    public void Destroy(Entity e)
    {
        if (!IsAlive(e))
            return;
        foreach (var store in _stores.Values)
            store.Remove(e.Index);
        _generations[e.Index]++;   // invalide les références existantes
        _alive[e.Index] = false;
        _freeSlots.Push(e.Index);
        EntityCount--;
    }

    public ComponentStore<T> Store<T>() where T : struct
    {
        if (!_stores.TryGetValue(typeof(T), out var store))
        {
            store = new ComponentStore<T>();
            _stores.Add(typeof(T), store);
        }
        return (ComponentStore<T>)store;
    }

    public IEnumerable<IComponentStore> Stores => _stores.Values;

    public ref T Add<T>(Entity e, in T component = default) where T : struct
    {
        EnsureAlive(e);
        return ref Store<T>().Set(e.Index, component);
    }

    public ref T Get<T>(Entity e) where T : struct
    {
        EnsureAlive(e);
        return ref Store<T>().Get(e.Index);
    }

    public bool Has<T>(Entity e) where T : struct => IsAlive(e) && Store<T>().Has(e.Index);

    public void Remove<T>(Entity e) where T : struct
    {
        if (IsAlive(e))
            Store<T>().Remove(e.Index);
    }

    /// <summary>Ressource globale unique par type (horloge, configuration, RNG, données statiques…).</summary>
    public T Resource<T>() where T : class =>
        _resources.TryGetValue(typeof(T), out var r) ? (T)r : throw new KeyNotFoundException($"Ressource {typeof(T).Name} absente");

    public void SetResource<T>(T resource) where T : class => _resources[typeof(T)] = resource;

    public bool TryResource<T>(out T resource) where T : class
    {
        if (_resources.TryGetValue(typeof(T), out var r))
        {
            resource = (T)r;
            return true;
        }
        resource = null!;
        return false;
    }

    /// <summary>
    /// Copie profonde des entités et des composants (structs blittables, copie de tableaux).
    /// Les ressources qui changent pendant la partie (RNG, état de la bataille…) implémentent
    /// <see cref="ICloneableResource"/> et sont copiées ; les autres (données statiques) sont partagées.
    /// </summary>
    public World Clone()
    {
        var copy = new World
        {
            _generations = (int[])_generations.Clone(),
            _alive = (bool[])_alive.Clone(),
            _nextSlot = _nextSlot,
            EntityCount = EntityCount,
            Tick = Tick,
        };
        foreach (var slot in _freeSlots.Reverse())
            copy._freeSlots.Push(slot);
        foreach (var (type, store) in _stores)
            copy._stores.Add(type, store.Clone());
        foreach (var (type, res) in _resources)
            copy._resources.Add(type, res is ICloneableResource c ? c.CloneResource() : res);
        return copy;
    }

    void EnsureAlive(Entity e)
    {
        if (!IsAlive(e))
            throw new InvalidOperationException($"{e} n'existe plus");
    }
}

/// <summary>Ressource dont l'état évolue pendant la partie : copiée par <see cref="World.Clone"/> (instantanés, projections).</summary>
public interface ICloneableResource
{
    object CloneResource();
}
