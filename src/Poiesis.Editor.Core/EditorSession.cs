using Poiesis.Engine.Ecs;
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
        History.Changed += Touch;
    }

    /// <summary>
    /// Révision du document : augmente à chaque modification. L'aperçu, le titre (« modifié ») et les panneaux
    /// s'en servent pour savoir quand se mettre à jour.
    /// </summary>
    public int Revision { get; private set; }

    int _savedRevision;

    public bool IsDirty => Revision != _savedRevision;

    /// <summary>Signale une modification faite hors commande (glissement en cours dans l'inspecteur ou le viewport).</summary>
    public void Touch() => Revision++;

    /// <summary>Monde d'aperçu du mode édition : la scène cuite, sans sim. Reconstruit quand la révision change.</summary>
    public World? Preview { get; private set; }

    public BakeContext? PreviewBake { get; private set; }

    /// <summary>Erreur de la dernière cuisson d'aperçu (ex. classe de vaisseau inconnue), affichée dans le viewport.</summary>
    public string? PreviewError { get; private set; }

    int _previewRevision = -1;
    int _previewGeneration = -1;

    public void UpdatePreview()
    {
        var module = Modules.Module;
        if (module is null || (_previewRevision == Revision && _previewGeneration == Modules.Generation))
            return;
        _previewRevision = Revision;
        _previewGeneration = Modules.Generation;
        try
        {
            var world = new World();
            module.InitializeWorld(world);
            PreviewBake = SceneBaker.Bake(Scene, world);
            Preview = world;
            PreviewError = null;
        }
        catch (Exception ex)
        {
            Preview = null;
            PreviewBake = null;
            PreviewError = ex.Message;
        }
    }

    public Node? FindNode(Guid id) => Scene.DescendantsAndSelf().FirstOrDefault(n => n.Id == id);

    /// <summary>Copie profonde d'un nœud et de ses enfants, avec de nouveaux identifiants.</summary>
    public Node CloneNode(Node node)
    {
        var copy = Serializer.Read(Serializer.Write(node));
        foreach (var n in copy.DescendantsAndSelf())
            n.Id = Guid.NewGuid();
        copy.Name = node.Name + " (copie)";
        return copy;
    }

    public string ProjectRoot { get; }

    public NodeTypeRegistry NodeTypes { get; }

    public SceneSerializer Serializer { get; }

    public GameModuleHost Modules { get; }

    public UndoStack History { get; } = new();

    public Node Scene { get; private set; }

    public string? ScenePath { get; private set; }

    public Node? Selection { get; set; }

    /// <summary>Entité sélectionnée dans la partie en cours (slot), ou -1 : inspecteur d'entités et dessins de débogage.</summary>
    public int SelectedEntity { get; set; } = -1;

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
        bool wasDirty = IsDirty;
        var (json, selectedId) = DetachScene();

        bool unloaded = Modules.Unload();

        Modules.Load(assemblyPath);
        Scene = Serializer.Read(json);
        Selection = selectedId is { } id ? Scene.DescendantsAndSelf().FirstOrDefault(n => n.Id == id) : null;
        if (!wasDirty)
            _savedRevision = Revision;   // recharger le code ne modifie pas le document
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
        Preview = null;
        PreviewBake = null;
        return result;
    }

    public void NewScene()
    {
        Scene = new SceneRoot { Name = "Nouvelle scène" };
        ScenePath = null;
        Selection = null;
        History.Clear();
        _savedRevision = Revision;
    }

    public void OpenScene(string path)
    {
        Scene = Serializer.Read(File.ReadAllText(path));
        ScenePath = Path.GetFullPath(path);
        Selection = null;
        History.Clear();
        _savedRevision = Revision;
    }

    public void SaveScene(string? path = null)
    {
        path ??= ScenePath ?? throw new InvalidOperationException("Pas de chemin pour la scène");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serializer.Write(Scene) + "\n");
        ScenePath = Path.GetFullPath(path);
        _savedRevision = Revision;
    }

    /// <summary>Play : la scène est cuite dans un monde neuf. Elle-même n'est jamais modifiée par la partie.</summary>
    public void StartPlay()
    {
        var module = Modules.Module ?? throw new InvalidOperationException("Aucun module de jeu chargé");
        Play = new PlaySession(Scene, module);
        PlayState = PlayState.Playing;
    }

    /// <summary>En pause : avance d'un pas de sim exactement.</summary>
    public void StepOnce()
    {
        if (Play is not null && PlayState == PlayState.Paused)
            Play.Run(1);
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
        SelectedEntity = -1;
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
