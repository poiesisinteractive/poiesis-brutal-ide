using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Poiesis.Engine.Scene;

namespace Poiesis.Engine.Hosting;

/// <summary>
/// Charge l'assembly d'un jeu dans un <see cref="AssemblyLoadContext"/> collectible, et la recharge à chaud.
/// Les assemblies du moteur (Poiesis.Engine.*, Brutal.*) restent dans le contexte par défaut et sont partagées,
/// pour que les types du contrat (<see cref="IGameModule"/>, composants de base) soient les mêmes des deux côtés.
/// </summary>
public sealed class GameModuleHost(NodeTypeRegistry nodeTypes, string contentRoot) : IDisposable
{
    GameLoadContext? _context;
    WeakReference? _lastContext;

    public IGameModule? Module { get; private set; }

    public Assembly? Assembly { get; private set; }

    public int Generation { get; private set; }

    public event Action<IGameModule>? Loaded;
    public event Action? Unloading;

    /// <summary>Charge (ou recharge) le module depuis une DLL compilée.</summary>
    public IGameModule Load(string assemblyPath)
    {
        Unload();

        // On charge depuis un flux : le fichier n'est pas verrouillé, le build suivant peut l'écraser.
        _context = new GameLoadContext(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        using (var dll = File.OpenRead(assemblyPath))
        {
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            using var pdb = File.Exists(pdbPath) ? File.OpenRead(pdbPath) : null;
            Assembly = _context.LoadFromStream(dll, pdb);
        }

        var moduleType = Assembly.GetTypes().SingleOrDefault(t => typeof(IGameModule).IsAssignableFrom(t) && !t.IsAbstract)
            ?? throw new InvalidOperationException($"{assemblyPath} ne contient pas d'implémentation de IGameModule");
        Module = (IGameModule)Activator.CreateInstance(moduleType)!;

        nodeTypes.RegisterAssembly(Assembly);
        Module.Load(new GameModuleContext(nodeTypes, contentRoot));
        Generation++;
        Loaded?.Invoke(Module);
        return Module;
    }

    /// <summary>
    /// Décharge le module. Rend <c>false</c> si l'assembly est encore retenue après plusieurs GC
    /// (fuite : un objet du jeu est encore référencé par l'éditeur).
    /// </summary>
    public bool Unload()
    {
        if (_context is null)
            return true;

        DetachAndUnload();

        // Cette méthode ne doit toucher aucun objet du jeu : le code non optimisé (Debug, ou Tier 0 du JIT)
        // garde les temporaires d'un cadre vivants jusqu'à sa fin, ce qui empêcherait la collecte.
        // Le déchargement se fait en plusieurs temps (finaliseur du LoaderAllocator, puis ses poignées) :
        // il faut plusieurs cycles de GC, d'autant plus que le tas est gros.
        for (int i = 0; _lastContext!.IsAlive && i < 40; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
        return !_lastContext.IsAlive;
    }

    /// <summary>
    /// Vrai si l'assembly de la génération précédente est encore en mémoire. À vérifier un peu après
    /// <see cref="Unload"/> : une référence oubliée par l'éditeur ou par le jeu l'empêche de partir.
    /// </summary>
    public bool PreviousGenerationAlive
    {
        get
        {
            if (_lastContext is not { IsAlive: true })
                return false;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return _lastContext.IsAlive;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void DetachAndUnload()
    {
        Unloading?.Invoke();
        Module!.Unload(new GameModuleContext(nodeTypes, contentRoot));
        nodeTypes.UnregisterAssembly(Assembly!);
        _lastContext = new WeakReference(_context);
        Module = null;
        Assembly = null;
        _context!.Unload();
        _context = null;
    }

    public void Dispose() => Unload();

    sealed class GameLoadContext(string directory) : AssemblyLoadContext($"poiesis-game-{Guid.NewGuid():N}", isCollectible: true)
    {
        readonly AssemblyDependencyResolver? _resolver =
            Directory.EnumerateFiles(directory, "*.deps.json").FirstOrDefault() is { } deps
                ? new AssemblyDependencyResolver(deps.Replace(".deps.json", ".dll"))
                : null;

        protected override Assembly? Load(AssemblyName name)
        {
            // Moteur et BRUTAL : toujours partagés avec l'éditeur.
            if (name.Name is { } n && (n.StartsWith("Poiesis.Engine", StringComparison.Ordinal) || n.StartsWith("Brutal", StringComparison.Ordinal)))
                return null;
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name))
                return null;
            var path = _resolver?.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
