namespace Poiesis.Engine.Ecs;

/// <summary>Itération sur les entités qui ont deux composants, dans l'ordre déterministe des slots.</summary>
public static class Query
{
    public delegate void RefAction<T1>(int entity, ref T1 a);
    public delegate void RefAction<T1, T2>(int entity, ref T1 a, ref T2 b);

    public static void Each<T1>(World world, RefAction<T1> action) where T1 : struct
    {
        var s1 = world.Store<T1>();
        var entities = s1.DenseEntities;
        var items = s1.Components;
        for (int i = 0; i < entities.Length; i++)
            action(entities[i], ref items[i]);
    }

    public static void Each<T1, T2>(World world, RefAction<T1, T2> action)
        where T1 : struct
        where T2 : struct
    {
        var s1 = world.Store<T1>();
        var s2 = world.Store<T2>();
        // On parcourt le plus petit stockage.
        if (s1.Count <= s2.Count)
        {
            var entities = s1.DenseEntities;
            var items = s1.Components;
            for (int i = 0; i < entities.Length; i++)
                if (s2.Has(entities[i]))
                    action(entities[i], ref items[i], ref s2.Get(entities[i]));
        }
        else
        {
            var entities = s2.DenseEntities;
            var items = s2.Components;
            for (int i = 0; i < entities.Length; i++)
                if (s1.Has(entities[i]))
                    action(entities[i], ref s1.Get(entities[i]), ref items[i]);
        }
    }
}
