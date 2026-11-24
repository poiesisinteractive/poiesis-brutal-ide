using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Hosting;

/// <summary>
/// Une partie en cours, dans le process de l'éditeur ou en headless : la scène est cuite dans un monde neuf,
/// la sim avance à pas fixe. Au Stop, on jette le monde ; la scène éditée n'a jamais été touchée.
/// </summary>
public sealed class PlaySession
{
    public PlaySession(Node scene, IGameModule module)
    {
        World = new World();
        module.InitializeWorld(World);
        Bake = SceneBaker.Bake(scene, World);
        module.BuildSchedule(World, Schedule);
    }

    public World World { get; private set; }

    public BakeContext Bake { get; }

    public Schedule Schedule { get; } = new();

    public FixedStepClock Clock { get; } = new();

    /// <summary>Avance la sim selon le temps réel écoulé ; rend le nombre de pas exécutés.</summary>
    public int Update(double realDeltaSeconds)
    {
        int steps = Clock.Advance(realDeltaSeconds);
        for (int i = 0; i < steps; i++)
            Schedule.Step(World, Clock.StepSeconds);
        return steps;
    }

    /// <summary>Avance d'un nombre exact de pas, sans horloge (headless, tests, projections, pas-à-pas en pause).</summary>
    public void Run(int steps)
    {
        for (int i = 0; i < steps; i++)
            Schedule.Step(World, Clock.StepSeconds);
    }

    /// <summary>Temps de jeu écoulé, en secondes.</summary>
    public double Time => World.Tick * Clock.StepSeconds;

    readonly List<Snapshot> _snapshots = new();

    public IReadOnlyList<Snapshot> Snapshots => _snapshots;

    /// <summary>Photographie la partie (copie profonde du monde). Les systèmes doivent être sans état propre.</summary>
    public Snapshot TakeSnapshot(string? name = null)
    {
        var snap = new Snapshot(name ?? $"t = {Time:0.0} s", World.Tick, World.Clone());
        _snapshots.Add(snap);
        return snap;
    }

    /// <summary>Revient à un instantané. L'instantané reste intact : on peut y revenir plusieurs fois.</summary>
    public void Restore(Snapshot snapshot) => World = snapshot.World.Clone();

    public void DeleteSnapshot(Snapshot snapshot) => _snapshots.Remove(snapshot);
}

/// <summary>Un instantané de partie : son nom, son pas et une copie du monde.</summary>
public sealed record Snapshot(string Name, long Tick, World World);
