using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Parallel;
using Poiesis.Engine.Scene;

namespace Orionum.Game.Tests;

/// <summary>Tests de la sim, sur le module référencé directement (sans chargement dynamique).</summary>
[Collection("sim")]   // ces tests changent Jobs.Shared : pas en parallèle entre eux
public class BattleTests
{
    static (OrionumModule Module, NodeTypeRegistry Registry) LoadModule()
    {
        var registry = new NodeTypeRegistry();
        registry.RegisterAssembly(typeof(OrionumModule).Assembly);
        var module = new OrionumModule();
        module.Load(new GameModuleContext(registry, GameModuleTests.ContentRoot));
        return (module, registry);
    }

    static PlaySession Start(string scene, Action<Node>? edit = null)
    {
        var (module, registry) = LoadModule();
        var root = new SceneSerializer(registry, GameModuleTests.ContentRoot).Read(File.ReadAllText(GameModuleTests.Scene(scene)));
        edit?.Invoke(root);
        return new PlaySession(root, module);
    }

    /// <summary>Empreinte de l'état : positions, coques, boucliers et projectiles, au bit près.</summary>
    static string Fingerprint(World world)
    {
        var hash = new HashCode();
        var ships = world.Store<Ship>();
        var dense = ships.DenseEntities;
        for (int i = 0; i < dense.Length; i++)
        {
            int e = dense[i];
            var p = world.Store<Transform>().Get(e).Position;
            hash.Add(e);
            hash.Add(p.X); hash.Add(p.Y); hash.Add(p.Z);
            hash.Add(ships.Components[i].Hull);
            hash.Add(world.Store<Shields>().Get(e).Total);
        }
        var battle = world.Resource<Battle>();
        hash.Add(battle.ProjectileCount);
        hash.Add(battle.ShotsFired);
        return $"{dense.Length}:{hash.ToHashCode():X8}";
    }

    [Fact]
    public void The_capital_battle_ends_with_a_winner_within_twenty_minutes()
    {
        var session = Start("capital-battle");
        var battle = session.World.Resource<Battle>();
        for (int i = 0; i < 20 * 60 * 20 && battle.Winner is null; i += 200)
            session.Run(200);

        Assert.NotNull(battle.Winner);
        Assert.True(battle.TurretsDestroyed > 0, "les dégâts localisés détruisent des tourelles");
        Assert.True(battle.MissilesIntercepted > 0, "la défense ponctuelle abat des missiles");
    }

    [Fact]
    public void Same_seed_gives_the_same_battle_whatever_the_number_of_threads()
    {
        var previous = Jobs.Shared;
        try
        {
            Jobs.Shared = new Jobs(1);
            var single = Start("capital-battle");
            single.Run(20 * 240);

            Jobs.Shared = new Jobs(4);
            var parallel = Start("capital-battle");
            parallel.Run(20 * 240);

            Assert.Equal(Fingerprint(single.World), Fingerprint(parallel.World));
        }
        finally
        {
            Jobs.Shared = previous;
        }
    }

    [Fact]
    public void Restoring_a_snapshot_replays_exactly_the_same_future()
    {
        var session = Start("capital-battle");
        session.Run(20 * 150);   // en plein combat
        var snap = session.TakeSnapshot();
        session.Run(20 * 30);
        var first = Fingerprint(session.World);

        session.Restore(snap);
        Assert.Equal(snap.Tick, session.World.Tick);
        session.Run(20 * 30);

        Assert.Equal(first, Fingerprint(session.World));
    }

    [Fact]
    public void A_shot_is_absorbed_by_the_face_it_hits_and_only_the_overflow_reaches_the_hull()
    {
        var session = Start("capital-battle");
        var world = session.World;
        var catalog = world.Resource<Catalog>();
        var battle = world.Resource<Battle>();
        var ships = world.Store<Ship>();
        int e = ships.DenseEntities[0];
        var target = world.EntityAt(e);
        var cls = catalog.Classes[ships.Get(e).Class].Def;
        var fwd = world.Store<Motion>().Get(e).Forward;

        // Un coup venant de l'avant, plus fort que la face avant.
        float amount = cls.ShieldFace + 1000;
        Damage.Apply(world, catalog, battle, new Hit { Target = target, Amount = amount, FromDir = fwd, VsShield = 1, VsHull = 1 });

        var shields = world.Store<Shields>().Get(e);
        Assert.Equal(0, shields.Front);
        Assert.Equal(cls.ShieldFace, shields.Back);
        Assert.Equal(cls.Hull - 1000, ships.Get(e).Hull, 1);
    }

    [Fact]
    public void Hull_damage_wears_the_nearest_turret_until_it_breaks()
    {
        var session = Start("capital-battle");
        var world = session.World;
        var catalog = world.Resource<Catalog>();
        var battle = world.Resource<Battle>();
        var ships = world.Store<Ship>();
        // Le vaisseau amiral de la Coalition : bordées à bâbord (+X local).
        int e = ships.DenseEntities.ToArray().First(i => catalog.Classes[ships.Get(i).Class].Def.Role == Role.Flagship);
        var cls = catalog.Classes[ships.Get(e).Class];
        ref var shields = ref world.Store<Shields>().Get(e);
        shields = default;   // boucliers à terre
        var port = Frame.FromForward(world.Store<Motion>().Get(e).Forward).Port;
        int portBattery = Array.FindIndex(cls.Weapons, w => w.Def.Mount == Mount.Port);
        int before = world.Store<WeaponBank>().Get(e)[portBattery].AliveCount;

        float turretHp = cls.Weapons[portBattery].Def.TurretHp;
        Damage.Apply(world, catalog, battle, new Hit { Target = world.EntityAt(e), Amount = turretHp + 1, FromDir = port, VsShield = 1, VsHull = 1 });

        Assert.Equal(before - 1, world.Store<WeaponBank>().Get(e)[portBattery].AliveCount);
        Assert.Equal(1, battle.TurretsDestroyed);
    }

    [Fact]
    public void A_cruising_fleet_drops_out_of_cruise_when_the_enemy_gets_close()
    {
        var session = Start("capital-battle");
        var world = session.World;
        var ships = world.Store<Ship>();
        session.Run(20);
        Assert.True(ships.Components[0].Cruising, "à 120 km, les flottes sont en croisière");
        for (int i = 0; i < 20 * 60 && ships.Components[0].Cruising; i++)
            session.Run(1);
        Assert.False(ships.Components[0].Cruising);
        Assert.True(world.Store<Motion>().Get(ships.DenseEntities[0]).Velocity.Length() <= 200,
            "à la sortie de croisière, la coque reprend sa vitesse de combat");
    }

    [Fact]
    public void A_series_plays_each_seed_to_the_end_and_aggregates_the_game_report()
    {
        var (module, registry) = LoadModule();
        var json = File.ReadAllText(GameModuleTests.Scene("capital-battle"));
        var serializer = new SceneSerializer(registry, GameModuleTests.ContentRoot);

        var series = Series.Run("capital-battle", () => serializer.Read(json), module, firstSeed: 7, runs: 2, maxSeconds: 1200);

        Assert.Equal([7, 8], series.Runs.Select(r => r.Seed));
        Assert.All(series.Runs, r => Assert.True(r.Report.Finished));
        Assert.Equal(1.0, series.WinRates.Values.Sum(), 6);
        Assert.Contains(series.Stats, s => s.Name == "tourelles détruites" && s.Mean > 0);
        Assert.Contains("Victoires", series.Summary());
    }

    [Fact]
    public void The_seed_of_a_scene_is_found_by_convention_and_changes_the_battle()
    {
        var (module, registry) = LoadModule();
        var json = File.ReadAllText(GameModuleTests.Scene("capital-battle"));
        var serializer = new SceneSerializer(registry, GameModuleTests.ContentRoot);
        string Play(int seed)
        {
            var scene = serializer.Read(json);
            Assert.True(Series.SetSeed(scene, seed));
            var session = new PlaySession(scene, module);
            session.Run(20 * 120);
            return Fingerprint(session.World);
        }
        Assert.Equal(Play(3), Play(3));
        Assert.NotEqual(Play(3), Play(4));
    }

    [Fact]
    public void Five_hundred_against_five_hundred_runs_within_the_real_time_budget()
    {
        var session = Start("great-battle");
        Assert.Equal(1000 + 2, session.World.EntityCount);
        session.Run(20 * 90);   // jusqu'au contact
        var sw = System.Diagnostics.Stopwatch.StartNew();
        session.Run(20 * 30);
        double msPerStep = sw.Elapsed.TotalMilliseconds / (20 * 30);
        // Budget temps réel : 50 ms par pas (20 Hz). Large marge pour les machines de CI lentes et le Debug.
        Assert.True(msPerStep < 50, $"{msPerStep:0.0} ms par pas");
    }
}

[CollectionDefinition("sim", DisableParallelization = true)]
public class SimCollection;
