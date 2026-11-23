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

    public World World { get; }

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

    /// <summary>Avance d'un nombre exact de pas, sans horloge (headless, tests, projections).</summary>
    public void Run(int steps)
    {
        for (int i = 0; i < steps; i++)
            Schedule.Step(World, Clock.StepSeconds);
    }
}
