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

/// <summary>Temps passé dans un système : dernier pas, moyenne glissante et historique (profileur de l'éditeur).</summary>
public sealed class SystemTiming(string name)
{
    public const int HistoryLength = 240;

    readonly float[] _history = new float[HistoryLength];
    int _head;

    public string Name { get; } = name;
    public double LastMs { get; private set; }
    public double AverageMs { get; private set; }
    public double MaxMs { get; private set; }

    /// <summary>Historique en millisecondes, du plus ancien au plus récent.</summary>
    public float[] History()
    {
        var copy = new float[HistoryLength];
        for (int i = 0; i < HistoryLength; i++)
            copy[i] = _history[(_head + i) % HistoryLength];
        return copy;
    }

    internal void Record(double ms)
    {
        LastMs = ms;
        AverageMs = AverageMs == 0 ? ms : AverageMs * 0.95 + ms * 0.05;
        MaxMs = Math.Max(MaxMs * 0.999, ms);
        _history[_head] = (float)ms;
        _head = (_head + 1) % HistoryLength;
    }
}

/// <summary>Ordonnancement des systèmes : une liste ordonnée, exécutée à chaque pas, chronométrée système par système.</summary>
public sealed class Schedule
{
    readonly List<ISystem> _systems = new();
    readonly List<SystemTiming> _timings = new();

    public IReadOnlyList<ISystem> Systems => _systems;

    /// <summary>Temps par système, dans l'ordre d'exécution, et le total du pas.</summary>
    public IReadOnlyList<SystemTiming> Timings => _timings;

    public SystemTiming Total { get; } = new("Total");

    public Schedule Add(ISystem system)
    {
        _systems.Add(system);
        _timings.Add(new SystemTiming(system.Name));
        return this;
    }

    public void Clear()
    {
        _systems.Clear();
        _timings.Clear();
    }

    public void Step(World world, double dt)
    {
        var ctx = new TickContext(world.Tick, dt);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long t0 = start;
        for (int i = 0; i < _systems.Count; i++)
        {
            _systems[i].Update(world, ctx);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            _timings[i].Record(System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            t0 = t1;
        }
        Total.Record(System.Diagnostics.Stopwatch.GetElapsedTime(start, t0).TotalMilliseconds);
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
