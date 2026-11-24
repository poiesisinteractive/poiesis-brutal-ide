using Brutal.Concurrency.Jobs;

namespace Poiesis.Engine.Parallel;

/// <summary>
/// Boucles parallèles sur le <see cref="JobScheduler"/> de BRUTAL, découpées en tranches fixes.
/// </summary>
/// <remarks>
/// <para>Déterminisme : le découpage ne dépend que du nombre d'éléments et de la taille de tranche, jamais du nombre
/// de threads. Un corps de boucle ne doit écrire que dans ses propres éléments ; les effets croisés passent par des
/// tampons par tranche (<see cref="ChunkCount"/>), fusionnés ensuite dans l'ordre des tranches.</para>
/// <para>Hot reload : les tâches sont des objets du moteur ; le délégué du jeu est lâché dès la fin de la boucle,
/// pour que l'ordonnanceur ne retienne rien de l'assembly du jeu.</para>
/// </remarks>
public sealed class Jobs : IDisposable
{
    static Jobs? _shared;

    readonly JobScheduler? _scheduler;
    readonly List<RangeJob> _pool = new();
    readonly object _lock = new();

    public Jobs(int workers)
    {
        Workers = workers;
        if (workers > 1)
            _scheduler = new JobScheduler((uint)workers, "poiesis-jobs");
    }

    /// <summary>
    /// Les jobs partagés du processus (un thread de moins que de cœurs). Remplaçable, par exemple pour vérifier
    /// qu'une sim donne le même résultat sur un seul thread.
    /// </summary>
    public static Jobs Shared
    {
        get => LazyInitializer.EnsureInitialized(ref _shared, () => new Jobs(Math.Max(1, Environment.ProcessorCount - 1)));
        set => _shared = value;
    }

    public int Workers { get; }

    /// <summary>Nombre de tranches pour <paramref name="count"/> éléments.</summary>
    public static int ChunkCount(int count, int chunkSize) => (count + chunkSize - 1) / chunkSize;

    /// <summary>
    /// Exécute <paramref name="body"/>(indice de tranche, début, fin) sur toutes les tranches de
    /// <paramref name="chunkSize"/> éléments, en parallèle si cela vaut la peine, puis attend la fin.
    /// </summary>
    public void For(int count, int chunkSize, Action<int, int, int> body)
    {
        int chunks = ChunkCount(count, chunkSize);
        if (chunks == 0)
            return;
        if (chunks == 1 || _scheduler is null)
        {
            for (int c = 0; c < chunks; c++)
                body(c, c * chunkSize, Math.Min(count, (c + 1) * chunkSize));
            return;
        }

        lock (_lock)   // un seul For à la fois sur l'ordonnanceur partagé
        {
            while (_pool.Count < chunks)
                _pool.Add(new RangeJob());
            for (int c = 0; c < chunks; c++)
            {
                var job = _pool[c];
                job.Body = body;
                job.Chunk = c;
                job.Start = c * chunkSize;
                job.End = Math.Min(count, (c + 1) * chunkSize);
                job.Error = null;
                _scheduler.Queue(job, job.End - job.Start);
            }
            _scheduler.ExecuteJobs();
            _scheduler.Wait();

            Exception? error = null;
            for (int c = 0; c < chunks; c++)
            {
                error ??= _pool[c].Error;
                _pool[c].Body = null;
                _pool[c].Error = null;
            }
            if (error is not null)
                throw new AggregateException("Erreur dans une tranche parallèle", error);
        }
    }

    public void Dispose() => _scheduler?.AbortJobs(waitForCompletion: true);

    sealed class RangeJob : IJob
    {
        public Action<int, int, int>? Body;
        public int Chunk, Start, End;
        public Exception? Error;

        public void Run()
        {
            try
            {
                Body!(Chunk, Start, End);
            }
            catch (Exception ex)
            {
                Error = ex;
            }
        }
    }
}
