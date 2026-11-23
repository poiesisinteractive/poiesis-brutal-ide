using Poiesis.Engine.Ecs;

namespace Poiesis.Engine.Tests;

public class EcsTests
{
    struct Health { public float Value; }
    struct Speed { public float Value; }

    [Fact]
    public void Destroyed_entity_is_no_longer_alive_and_its_slot_is_reused_with_a_new_generation()
    {
        var world = new World();
        var a = world.Create();
        world.Add(a, new Health { Value = 10 });
        world.Destroy(a);

        Assert.False(world.IsAlive(a));
        Assert.False(world.Store<Health>().Has(a.Index));
        Assert.True(world.EntityAt(a.Index).IsNull);

        var b = world.Create();
        Assert.Equal(a.Index, b.Index);
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.Equal(b, world.EntityAt(b.Index));
    }

    [Fact]
    public void Iteration_follows_slot_order_whatever_the_insertion_order()
    {
        var world = new World();
        var e = Enumerable.Range(0, 5).Select(_ => world.Create()).ToArray();
        foreach (var i in new[] { 3, 0, 4, 1, 2 })
            world.Add(e[i], new Health { Value = i });

        var seen = new List<int>();
        Query.Each<Health>(world, (int idx, ref Health _) => seen.Add(idx));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, seen);

        world.Remove<Health>(e[2]);
        seen.Clear();
        Query.Each<Health>(world, (int idx, ref Health _) => seen.Add(idx));
        Assert.Equal(new[] { 0, 1, 3, 4 }, seen);
    }

    [Fact]
    public void Two_component_query_visits_only_entities_that_have_both()
    {
        var world = new World();
        var a = world.Create();
        var b = world.Create();
        world.Add(a, new Health { Value = 1 });
        world.Add(a, new Speed { Value = 2 });
        world.Add(b, new Health { Value = 3 });

        int count = 0;
        Query.Each<Health, Speed>(world, (int _, ref Health h, ref Speed s) =>
        {
            count++;
            h.Value += s.Value;
        });

        Assert.Equal(1, count);
        Assert.Equal(3, world.Get<Health>(a).Value);
    }

    [Fact]
    public void Clone_is_deep_for_components()
    {
        var world = new World();
        var a = world.Create();
        world.Add(a, new Health { Value = 5 });

        var copy = world.Clone();
        copy.Get<Health>(a).Value = 99;

        Assert.Equal(5, world.Get<Health>(a).Value);
        Assert.Equal(99, copy.Get<Health>(a).Value);
    }

    [Fact]
    public void Fixed_step_clock_drops_the_backlog_when_the_machine_cannot_keep_up()
    {
        var clock = new FixedStepClock(0.05, maxStepsPerFrame: 4);
        Assert.Equal(2, clock.Advance(0.1));
        Assert.Equal(4, clock.Advance(10));
        Assert.Equal(0, clock.Advance(0.01));
        clock.TimeScale = 0;
        Assert.Equal(0, clock.Advance(1));
    }
}
