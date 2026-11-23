using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor;

public enum PlayState { Editing, Playing, Paused }

/// <summary>
/// L'état de l'éditeur pour un projet ouvert : la scène éditée, la sélection, l'historique, le module de jeu
/// chargé à chaud et la partie en cours. Les panneaux ImGui lisent et modifient cet objet, rien d'autre.
/// </summary>
public sealed class EditorSession : IDisposable
{
    public EditorSession(string projectRoot)
    {
        ProjectRoot = projectRoot;
        NodeTypes = new NodeTypeRegistry();
        Serializer = new SceneSerializer(NodeTypes);
        Modules = new GameModuleHost(NodeTypes, Path.Combine(projectRoot, "content"));
        Scene = new SceneRoot { Name = "Nouvelle scène" };
    }

    public string ProjectRoot { get; }

    public NodeTypeRegistry NodeTypes { get; }

    public SceneSerializer Serializer { get; }

    public GameModuleHost Modules { get; }

    public UndoStack History { get; } = new();

    public Node Scene { get; private set; }

    public string? ScenePath { get; private set; }

    public Node? Selection { get; set; }

    public PlayState PlayState { get; private set; } = PlayState.Editing;

    public PlaySession? Play { get; private set; }

    public event Action<string>? Log;

    public void LoadGameModule(string assemblyPath)
    {
        Modules.Load(assemblyPath);
        Log?.Invoke($"Module chargé : {Modules.Module!.Name} (génération {Modules.Generation})");
    }

    /// <summary>
    /// Recharge le module de jeu après un build (le sien ou celui de l'agent IA). La scène est sérialisée
    /// avant le déchargement, car ses nœuds sont des instances des types de l'ancienne assembly, puis relue
    /// avec les nouveaux types. Une partie en cours est arrêtée : l'état ECS n'est pas migré au jalon 1.
    /// </summary>
    public bool HotReload(string assemblyPath)
    {
        var (json, selectedId) = DetachScene();

        bool unloaded = Modules.Unload();
        if (!unloaded)
            Log?.Invoke("Attention : l'ancienne assembly du jeu est encore retenue en mémoire (fuite de références).");

        Modules.Load(assemblyPath);
        Scene = Serializer.Read(json);
        Selection = selectedId is { } id ? Scene.DescendantsAndSelf().FirstOrDefault(n => n.Id == id) : null;
        Log?.Invoke($"Rechargé à chaud : {Modules.Module!.Name} (génération {Modules.Generation})");
        return unloaded;
    }

    /// <summary>
    /// Sérialise la scène et lâche tout ce qui retient un type de l'ancienne assembly. Méthode à part et non inlinée :
    /// le code non optimisé garde les temporaires d'un cadre vivants, ce qui bloquerait le déchargement.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    (string Json, Guid? SelectedId) DetachScene()
    {
        if (PlayState != PlayState.Editing)
            Stop();
        var result = (Serializer.Write(Scene), Selection?.Id);
        Scene = new SceneRoot();
        Selection = null;
        History.Clear();   // les commandes retiennent des nœuds et des PropertyInfo
        Play = null;
        return result;
    }

    public void NewScene()
    {
        Scene = new SceneRoot { Name = "Nouvelle scène" };
        ScenePath = null;
        Selection = null;
        History.Clear();
    }

    public void OpenScene(string path)
    {
        Scene = Serializer.Read(File.ReadAllText(path));
        ScenePath = path;
        Selection = null;
        History.Clear();
    }

    public void SaveScene(string? path = null)
    {
        path ??= ScenePath ?? throw new InvalidOperationException("Pas de chemin pour la scène");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serializer.Write(Scene) + "\n");
        ScenePath = path;
    }

    /// <summary>Play : la scène est cuite dans un monde neuf. Elle-même n'est jamais modifiée par la partie.</summary>
    public void StartPlay()
    {
        var module = Modules.Module ?? throw new InvalidOperationException("Aucun module de jeu chargé");
        Play = new PlaySession(Scene, module);
        PlayState = PlayState.Playing;
    }

    public void TogglePause()
    {
        if (Play is null)
            return;
        PlayState = PlayState == PlayState.Paused ? PlayState.Playing : PlayState.Paused;
        Play.Clock.TimeScale = PlayState == PlayState.Paused ? 0 : 1;
    }

    public void Stop()
    {
        Play = null;
        PlayState = PlayState.Editing;
    }

    /// <summary>À appeler à chaque image.</summary>
    public void Update(double realDeltaSeconds)
    {
        if (Play is not null && PlayState == PlayState.Playing)
            Play.Update(realDeltaSeconds);
    }

    public void Dispose() => Modules.Dispose();
}
