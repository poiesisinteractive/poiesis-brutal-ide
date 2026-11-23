namespace Poiesis.Engine.Ecs;

/// <summary>Contexte d'un pas de sim à pas fixe.</summary>
public readonly record struct TickContext(long Tick, double Dt);

/// <summary>
/// Un système : une phase de logique qui lit et écrit des composants.
/// Le code de gameplay (écrit à la main ou par l'IA) prend presque toujours cette forme.
/// </summary>
public interface ISystem
{
    /// <summary>Nom affiché dans l'éditeur et dans les profils.</summary>
    string Name => GetType().Name;

    void Update(World world, in TickContext ctx);
}

/// <summary>Ordonnancement des systèmes : une liste ordonnée, exécutée à chaque pas.</summary>
public sealed class Schedule
{
    readonly List<ISystem> _systems = new();

    public IReadOnlyList<ISystem> Systems => _systems;

    public Schedule Add(ISystem system)
    {
        _systems.Add(system);
        return this;
    }

    public void Clear() => _systems.Clear();

    public void Step(World world, double dt)
    {
        var ctx = new TickContext(world.Tick, dt);
        foreach (var system in _systems)
            system.Update(world, ctx);
        world.Tick++;
    }
}

/// <summary>Accumulateur de temps : convertit le temps réel en pas fixes (20 Hz par défaut, comme le POC).</summary>
public sealed class FixedStepClock(double stepSeconds = 1.0 / 20.0, int maxStepsPerFrame = 8)
{
    double _accumulator;

    public double StepSeconds { get; } = stepSeconds;

    /// <summary>Multiplicateur de vitesse (pause = 0).</summary>
    public double TimeScale { get; set; } = 1.0;

    /// <summary>Rend le nombre de pas à exécuter pour cette image.</summary>
    public int Advance(double realDeltaSeconds)
    {
        _accumulator += realDeltaSeconds * TimeScale;
        int steps = (int)(_accumulator / StepSeconds);
        if (steps > maxStepsPerFrame)
        {
            steps = maxStepsPerFrame;
            _accumulator = 0;   // la machine ne suit plus : on lâche le retard plutôt que spiraler
        }
        else
        {
            _accumulator -= steps * StepSeconds;
        }
        return steps;
    }
}
