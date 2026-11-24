using Brutal.Numerics;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Parallel;
using Poiesis.Engine.Spatial;

namespace Poiesis.Engine.Tests;

public class SpatialTests
{
    [Fact]
    public void Query_returns_exactly_the_points_within_the_radius()
    {
        var rnd = new System.Random(7);
        int n = 2000;
        var indices = Enumerable.Range(0, n).Select(i => i * 3).ToArray();
        var points = Enumerable.Range(0, n).Select(_ => new float3(rnd.NextSingle() * 50_000 - 25_000, rnd.NextSingle() * 4000, rnd.NextSingle() * 50_000 - 25_000)).ToArray();
        var grid = new SpatialHash(2500);
        grid.Build(indices, points);

        for (int q = 0; q < 50; q++)
        {
            var center = points[rnd.Next(n)];
            float radius = 500 + rnd.NextSingle() * 9000;
            var found = new List<int>();
            grid.Query(center, radius, ref found, static (ref List<int> list, int index, float _) => list.Add(index));
            var expected = Enumerable.Range(0, n).Where(i => float3.Distance(points[i], center) <= radius).Select(i => indices[i]).Order();
            Assert.Equal(expected, found.Order());
        }
    }

    [Fact]
    public void Parallel_for_visits_every_element_once_in_fixed_chunks_and_rethrows_errors()
    {
        var jobs = new Jobs(4);
        var seen = new int[1000];
        jobs.For(1000, 64, (chunk, start, end) =>
        {
            Assert.Equal(chunk * 64, start);
            for (int i = start; i < end; i++)
                Interlocked.Increment(ref seen[i]);
        });
        Assert.All(seen, v => Assert.Equal(1, v));

        Assert.Throws<AggregateException>(() => jobs.For(100, 10, (c, _, _) =>
        {
            if (c == 3)
                throw new InvalidOperationException("boum");
        }));
    }

    sealed class Counter : ICloneableResource
    {
        public int Value;
        public object CloneResource() => new Counter { Value = Value };
    }

    sealed class Tick : ISystem
    {
        public void Update(World world, in TickContext ctx) => world.Resource<Counter>().Value++;
    }

    [Fact]
    public void Schedule_times_each_system_and_clones_mutable_resources()
    {
        var world = new World();
        world.SetResource(new Counter());
        var schedule = new Schedule().Add(new Tick());
        for (int i = 0; i < 10; i++)
            schedule.Step(world, 0.05);

        Assert.Single(schedule.Timings);
        Assert.Equal("Tick", schedule.Timings[0].Name);
        Assert.True(schedule.Total.LastMs >= 0);

        var copy = world.Clone();
        schedule.Step(world, 0.05);
        Assert.Equal(11, world.Resource<Counter>().Value);
        Assert.Equal(10, copy.Resource<Counter>().Value);
    }
}

public class LiveEditTests
{
    struct Inner { public float A, B; }
    struct Outer { public float Hull; public Inner Faces; }

    [Fact]
    public void A_nested_field_edited_through_boxed_copies_is_written_back_to_the_component()
    {
        var world = new World();
        var e = world.Create();
        world.Add(e, new Outer { Hull = 100, Faces = new Inner { A = 1, B = 2 } });
        var store = world.Store<Outer>();

        // Ce que fait l'inspecteur d'entités : copie boxée, champ imbriqué modifié sur sa propre copie, réécriture.
        object boxed = store.GetBoxed(e.Index);
        var facesField = typeof(Outer).GetField(nameof(Outer.Faces))!;
        object faces = facesField.GetValue(boxed)!;
        typeof(Inner).GetField(nameof(Inner.B))!.SetValue(faces, 42f);
        facesField.SetValue(boxed, faces);
        typeof(Outer).GetField(nameof(Outer.Hull))!.SetValue(boxed, 7f);
        store.SetBoxed(e.Index, boxed);

        Assert.Equal(7, world.Get<Outer>(e).Hull);
        Assert.Equal(42, world.Get<Outer>(e).Faces.B);
        Assert.Equal(1, world.Get<Outer>(e).Faces.A);
    }
}
