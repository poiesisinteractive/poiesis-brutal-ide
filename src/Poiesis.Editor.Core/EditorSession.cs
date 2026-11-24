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
        Serializer = new SceneSerializer(NodeTypes, Path.Combine(projectRoot, "content"));
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

    /// <summary>Copie profonde d'un nœud et de ses enfants, avec de nouveaux identifiants. Une instance reste une instance.</summary>
    public Node CloneNode(Node node)
    {
        var json = Serializer.ToJson(node);
        SceneSerializer.RegenerateIds(json);
        var copy = Serializer.FromJson(json, ScenePath);
        copy.Name = node.Name + " (copie)";
        return copy;
    }

    /// <summary>
    /// Le nœud tel qu'il est dans sa scène source, sans les surcharges de la scène ouverte, ou <c>null</c>
    /// s'il n'appartient à aucune instance. Sert à montrer et à rétablir les surcharges dans l'inspecteur.
    /// </summary>
    public Node? InstanceBaseline(Node node)
    {
        Node? outer = null;
        for (var n = node; n is not null; n = n.Parent)
            if (n.InstanceOf is not null)
                outer = n;
        if (outer is null)
            return null;

        var now = DateTime.UtcNow;
        if (!_baselines.TryGetValue(outer.Id, out var cached) || cached.Path != outer.InstanceOf
            || cached.Generation != Modules.Generation || now - cached.At > TimeSpan.FromSeconds(1))
        {
            Dictionary<Guid, Node> nodes;
            try
            {
                nodes = Serializer.Instantiate(outer.InstanceOf!, outer.Id).DescendantsAndSelf().ToDictionary(n => n.Id);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
            {
                nodes = new();
            }
            _baselines[outer.Id] = cached = (outer.InstanceOf!, Modules.Generation, now, nodes);
        }
        return cached.Nodes.GetValueOrDefault(node.Id);
    }

    readonly Dictionary<Guid, (string Path, int Generation, DateTime At, Dictionary<Guid, Node> Nodes)> _baselines = new();

    /// <summary>
    /// Fait d'une branche de la scène une scène réutilisable : l'écrit dans <paramref name="relativePath"/> (relatif à
    /// <c>content/</c>), puis la remplace par une instance de ce fichier. Le remplacement s'annule, pas l'écriture du fichier.
    /// </summary>
    public Node SaveBranchAsScene(Node node, string relativePath)
    {
        if (node.Parent is not { HasEditableChildren: true })
            throw new InvalidOperationException("Seul un nœud ordinaire de la scène (ni la racine, ni un nœud d'instance) devient une scène.");
        relativePath = relativePath.Replace('\\', '/');
        var file = Path.Combine(Serializer.ContentRoot!, relativePath);
        if (File.Exists(file))
            throw new IOException($"{relativePath} existe déjà");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Serializer.Write(node) + "\n");

        var instance = Serializer.Instantiate(relativePath, node.Id, ScenePath);
        instance.Name = node.Name;
        History.Execute(new ReplaceNodeCommand(node, instance, $"Enregistrer {node.Name} comme scène"));
        if (Selection == node)
            Selection = instance;
        return instance;
    }

    /// <summary>
    /// Instancie la scène <paramref name="relativePath"/> sous <paramref name="parent"/>. Refuse une scène qui contient
    /// déjà la scène ouverte (boucle).
    /// </summary>
    public Node InstantiateScene(string relativePath, Node parent)
    {
        if (!parent.HasEditableChildren)
            throw new InvalidOperationException($"On n'ajoute pas d'enfant à {parent.Name} : il vient d'une scène instanciée.");
        if (ScenePath is not null && Serializer.DependsOn(relativePath, Serializer.RelativePath(ScenePath)))
            throw new InvalidDataException($"{relativePath} contient déjà la scène ouverte : l'instancier ici ferait une boucle.");
        var instance = Serializer.Instantiate(relativePath, Guid.NewGuid(), ScenePath);
        History.Execute(new AddNodeCommand(parent, instance));
        Selection = instance;
        return instance;
    }

    /// <summary>
    /// Après le déplacement d'un fichier ou d'un dossier de contenu : met à jour les instances de la scène ouverte
    /// et son propre chemin. Le fichier sur disque a déjà été réécrit par <see cref="ContentLibrary.Move"/>.
    /// </summary>
    public void RemapContent(string from, string to)
    {
        foreach (var n in Scene.DescendantsAndSelf())
            if (n.InstanceOf is { } path && ContentLibrary.Remap(path, from, to) is { } moved)
                n.InstanceOf = moved;
        if (ScenePath is { } scene && ContentLibrary.Remap(Serializer.RelativePath(scene), from, to) is { } movedScene)
            ScenePath = Path.GetFullPath(Path.Combine(Serializer.ContentRoot!, movedScene));
        _baselines.Clear();
    }

    // ----- Propositions de l'IA sur la scène -----------------------------------------------------------

    /// <summary>Les modifications que l'IA propose sur la scène ouverte, à relire (fantômes dans le viewport).</summary>
    public ScenePlan? Plan { get; private set; }

    /// <summary>La scène telle qu'elle serait avec toutes les propositions, cuite pour l'aperçu.</summary>
    public World? PlanWorld { get; private set; }

    public BakeContext? PlanBake { get; private set; }

    /// <summary>Les propositions cochées : seules elles sont dessinées en fantôme et appliquées.</summary>
    public HashSet<SceneOp> PlanChosen { get; } = new();

    public void SetPlan(ScenePlan? plan)
    {
        Plan = plan;
        PlanChosen.Clear();
        PlanWorld = null;
        PlanBake = null;
        if (plan is null)
            return;
        PlanChosen.UnionWith(plan.Ops);
        if (Modules.Module is { } module)
        {
            try
            {
                var world = new World();
                module.InitializeWorld(world);
                PlanBake = SceneBaker.Bake(plan.Working, world);
                PlanWorld = world;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Aperçu de la proposition impossible : {ex.Message}");
            }
        }
    }

    /// <summary>Applique les propositions cochées en une commande annulable, puis oublie la proposition.</summary>
    public IReadOnlyList<string> ApplyPlan()
    {
        if (Plan is not { } plan)
            return [];
        var (command, skipped) = plan.Apply(plan.Ops.Where(PlanChosen.Contains));
        if (command is not null)
            History.Execute(command);
        SetPlan(null);
        return skipped;
    }

    // ----- Copier-coller -----------------------------------------------------------------------------

    const string ClipboardKind = "poiesis.nodes";

    /// <summary>Le texte à mettre dans le presse-papiers pour copier un nœud et ses enfants (une instance reste une instance).</summary>
    public string CopyNode(Node node) => CopyNodes([node]);

    /// <summary>Le texte du presse-papiers pour plusieurs nœuds (chacun avec ses enfants), dans l'ordre donné.</summary>
    public string CopyNodes(IEnumerable<Node> nodes)
    {
        var doc = new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = ClipboardKind,
            ["nodes"] = new System.Text.Json.Nodes.JsonArray(nodes.Select(n => (System.Text.Json.Nodes.JsonNode)Serializer.ToJson(n)).ToArray()),
        };
        return SceneSerializer.ToJsonString(doc);
    }

    /// <summary>Vrai si le texte vient de <see cref="CopyNode"/> (d'ici ou d'un autre éditeur).</summary>
    public static bool IsNodeClipboard(string? text) =>
        text is not null && text.TrimStart().StartsWith('{') && text.Contains(ClipboardKind, StringComparison.Ordinal);

    /// <summary>
    /// Colle des nœuds copiés, avec de nouveaux identifiants : après <paramref name="target"/> parmi ses frères, ou dans
    /// la scène s'il n'y a pas de cible. Si la cible vient d'une instance, dans le premier ancêtre dont on peut changer
    /// les enfants. Une seule commande annulable. Rend les nœuds collés.
    /// </summary>
    public IReadOnlyList<Node> Paste(string text, Node? target)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject;
        if (doc?["kind"]?.GetValue<string>() != ClipboardKind || doc["nodes"] is not System.Text.Json.Nodes.JsonArray items)
            throw new InvalidDataException("Le presse-papiers ne contient pas de nœuds Poiesis.");

        Node parent;
        int index;
        if (target is null || target == Scene)
            (parent, index) = (Scene, Scene.Children.Count);
        else
        {
            var anchor = target;
            while (anchor.Parent is { HasEditableChildren: false } p)
                anchor = p;
            parent = anchor.Parent ?? Scene;
            index = parent.IndexOf(anchor) + 1;
        }

        var current = ScenePath is null ? null : Serializer.RelativePath(ScenePath);
        var commands = new List<IEditorCommand>();
        var pasted = new List<Node>();
        foreach (var item in items.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var json = (System.Text.Json.Nodes.JsonObject)item.DeepClone();
            SceneSerializer.RegenerateIds(json);
            var node = Serializer.FromJson(json, ScenePath);
            if (current is not null && SceneSerializer.InstancedScenes(node).Any(path => Serializer.DependsOn(path, current)))
                throw new InvalidDataException($"{node.Name} instancie la scène ouverte : le coller ici ferait une boucle.");
            commands.Add(new InsertNodeCommand(parent, index + pasted.Count, node));
            pasted.Add(node);
        }
        if (pasted.Count == 0)
            return pasted;
        History.Execute(new CompositeCommand(pasted.Count == 1 ? $"Coller {pasted[0].Name}" : $"Coller {pasted.Count} nœuds", commands));
        SetSelection(pasted);
        return pasted;
    }

    public string ProjectRoot { get; }

    public NodeTypeRegistry NodeTypes { get; }

    public SceneSerializer Serializer { get; }

    public GameModuleHost Modules { get; }

    public UndoStack History { get; } = new();

    public Node Scene { get; private set; }

    public string? ScenePath { get; private set; }

    readonly List<Node> _selected = new();

    /// <summary>
    /// Le nœud principal de la sélection (le dernier choisi) : inspecteur, gizmos, cadrage. L'affecter remplace toute
    /// la sélection par ce seul nœud.
    /// </summary>
    public Node? Selection
    {
        get => _selected.Count > 0 ? _selected[^1] : null;
        set
        {
            _selected.Clear();
            if (value is not null)
                _selected.Add(value);
        }
    }

    /// <summary>Tous les nœuds sélectionnés, le principal en dernier.</summary>
    public IReadOnlyList<Node> SelectedNodes => _selected;

    public bool IsSelected(Node node) => _selected.Contains(node);

    /// <summary>Ajoute un nœud à la sélection (il devient le principal), ou l'en retire s'il y était (Ctrl+clic).</summary>
    public void ToggleSelection(Node node)
    {
        if (!_selected.Remove(node))
            _selected.Add(node);
    }

    /// <summary>Remplace la sélection ; le dernier nœud devient le principal.</summary>
    public void SetSelection(IEnumerable<Node> nodes)
    {
        _selected.Clear();
        foreach (var n in nodes)
            if (!_selected.Contains(n))
                _selected.Add(n);
    }

    /// <summary>
    /// Les nœuds sélectionnés qu'on peut supprimer, dupliquer ou déplacer dans l'arbre : ni la racine, ni un nœud
    /// d'instance, ni un nœud dont un ancêtre est déjà sélectionné (il suit son parent).
    /// </summary>
    public IReadOnlyList<Node> SelectedRoots() =>
        _selected.Where(n => n.Parent is { HasEditableChildren: true } && !_selected.Any(o => o != n && o.IsSelfOrAncestorOf(n))).ToList();

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
        var (json, selectedIds) = DetachScene();

        bool unloaded = Modules.Unload();

        Modules.Load(assemblyPath);
        Scene = Serializer.Read(json, ScenePath);
        var byId = Scene.DescendantsAndSelf().ToDictionary(n => n.Id);
        SetSelection(selectedIds.Where(byId.ContainsKey).Select(id => byId[id]));
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
    (string Json, Guid[] SelectedIds) DetachScene()
    {
        if (PlayState != PlayState.Editing)
            Stop();
        var result = (Serializer.Write(Scene), _selected.Select(n => n.Id).ToArray());
        Scene = new SceneRoot();
        Selection = null;
        SetPlan(null);   // la proposition tient des nœuds de l'ancienne assembly
        _baselines.Clear();
        History.Clear();   // les commandes retiennent des nœuds et des PropertyInfo
        Play = null;
        Preview = null;
        PreviewBake = null;
        return result;
    }

    public void NewScene()
    {
        SetPlan(null);
        Scene = new SceneRoot { Name = "Nouvelle scène" };
        ScenePath = null;
        Selection = null;
        History.Clear();
        _savedRevision = Revision;
    }

    public void OpenScene(string path)
    {
        SetPlan(null);
        Scene = Serializer.Read(File.ReadAllText(path), path);
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
