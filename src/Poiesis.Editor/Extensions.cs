using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Editor.Api;
using Poiesis.Editor.Ui;
using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor;

/// <summary>
/// Les extensions d'éditeur du jeu : trouvées dans sa DLL au chargement du module, oubliées avant son déchargement
/// (hot reload). Dessine leurs panneaux, leurs sections d'inspecteur, leurs gizmos et leurs menus, et leur sert de
/// contexte : toute modification passe par l'historique d'annulation.
/// </summary>
sealed class ExtensionHost : IGizmoContext
{
    readonly EditorSession _session;
    readonly LogBuffer _log;
    readonly PropertyEditor _properties;
    EditorRegistry? _registry;
    readonly HashSet<object> _failed = new(ReferenceEqualityComparer.Instance);

    public ExtensionHost(EditorSession session, LogBuffer log, PropertyEditor properties)
    {
        _session = session;
        _log = log;
        _properties = properties;
        session.Modules.Loaded += _ => Discover();
        session.Modules.Unloading += Forget;
    }

    /// <summary>Noms des extensions chargées (pour l'aide et le journal).</summary>
    public IReadOnlyList<string> Names => _names;

    readonly List<string> _names = new();

    public IReadOnlyList<PanelExtension> Panels => _registry?.Panels ?? [];

    void Discover()
    {
        Forget();
        if (_session.Modules.Assembly is not { } assembly)
            return;
        var registry = new EditorRegistry();
        foreach (var type in assembly.GetTypes())
        {
            if (!typeof(IEditorExtension).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                continue;
            try
            {
                ((IEditorExtension)Activator.CreateInstance(type)!).Register(registry);
                _names.Add(type.Name);
            }
            catch (Exception ex)
            {
                _log.Error("extension", $"{type.Name} : {Inner(ex).Message}");
            }
        }
        _registry = registry;
        if (_names.Count > 0)
            _log.Info("extension", $"Extensions d'éditeur : {string.Join(", ", _names)} ({registry.Panels.Count} panneau(x), {registry.Inspectors.Count} inspecteur(s), {registry.Gizmos.Count} gizmo(s), {registry.Menus.Count} commande(s))");
    }

    /// <summary>Avant le déchargement : plus aucune référence vers un délégué ou un type du jeu.</summary>
    void Forget()
    {
        PreviewView?.Forget();
        _registry = null;
        _failed.Clear();
        _names.Clear();
        _drag = null;
        _dragEdits.Clear();
        _batch = null;
    }

    static Exception Inner(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    /// <summary>Appelle le code du jeu ; à la première exception, l'extension est coupée jusqu'au prochain rechargement.</summary>
    void Guard(object registration, string what, Action action)
    {
        if (_failed.Contains(registration))
            return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _failed.Add(registration);
            _log.Error("extension", $"{what} : {Inner(ex).GetType().Name} : {Inner(ex).Message} (désactivé jusqu'au prochain rechargement)");
        }
    }

    /// <summary>L'aperçu 3D partagé par les extensions (fourni par l'éditeur).</summary>
    public PreviewView? PreviewView { get; set; }

    /// <summary>Vrai quand une extension a demandé un rechargement du jeu (fait par l'éditeur à l'image suivante).</summary>
    public bool ReloadRequested { get; set; }

    public ThumbnailExtension? ThumbnailFor(string fileName) =>
        _registry?.Thumbnails.FirstOrDefault(t => !_failed.Contains(t) && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(t.Pattern, fileName));

    public World? BuildThumbnail(string fileName, string path)
    {
        if (ThumbnailFor(fileName) is not { } thumbnail)
            return null;
        World? world = null;
        Guard(thumbnail, $"Vignette « {thumbnail.Pattern} »", () => world = thumbnail.Build(path, this));
        return world;
    }

    // ----- Panneaux, inspecteurs, menus --------------------------------------------------------------

    public void DrawPanels(Action<PanelExtension>? beforeEach = null)
    {
        foreach (var panel in Panels)
        {
            beforeEach?.Invoke(panel);
            if (ImGui.Begin(panel.Title))
            {
                if (_failed.Contains(panel))
                    ImGui.TextColored(EditorTheme.Error, "Ce panneau a levé une exception (voir la console). Rechargez le jeu (Ctrl+B)."u8);
                else
                    Guard(panel, $"Panneau « {panel.Title} »", () => panel.Draw(this));
            }
            ImGui.End();
        }
    }

    public void DrawInspectors(Node node)
    {
        if (_registry is null)
            return;
        foreach (var inspector in _registry.Inspectors)
        {
            if (!inspector.NodeType.IsInstanceOfType(node))
                continue;
            ImGui.Spacing();
            ImGui.SeparatorText(inspector.Title);
            ImGui.PushID(inspector.Title);
            Guard(inspector, $"Inspecteur « {inspector.Title} »", () => inspector.Draw(node, this));
            ImGui.PopID();
        }
    }

    public bool HasMenus => _registry is { Menus.Count: > 0 };

    /// <summary>Le menu Outils : les commandes des extensions, rangées en sous-menus d'après leur chemin.</summary>
    public void DrawMenu()
    {
        if (_registry is null)
            return;
        foreach (var menu in _registry.Menus)
        {
            var parts = menu.Path.Split('/');
            int opened = 0;
            bool visible = true;
            for (; opened < parts.Length - 1; opened++)
                if (!ImGui.BeginMenu(parts[opened]))
                {
                    visible = false;
                    break;
                }
            if (visible && ImGui.MenuItem(parts[^1], ""u8, false, !_failed.Contains(menu)))
                Group(parts[^1], () => Guard(menu, $"Commande « {menu.Path} »", () => menu.Run(this)));
            for (int i = 0; i < opened; i++)
                ImGui.EndMenu();
        }
    }

    /// <summary>Lance une commande de menu par son chemin (scripts de vérification).</summary>
    public bool RunMenu(string path)
    {
        if (_registry?.Menus.FirstOrDefault(m => m.Path == path) is not { } menu)
            return false;
        Group(menu.Path, () => Guard(menu, $"Commande « {menu.Path} »", () => menu.Run(this)));
        return true;
    }

    // ----- Gizmos ------------------------------------------------------------------------------------

    readonly DrawContext _draw = new();
    WorldView? _view;
    bool _hovered;
    bool _consumed;
    string? _drag;            // poignée en cours de glissement
    float3 _dragPlanePoint, _dragPlaneNormal;
    readonly Dictionary<(Node, PropertyInfo), object?> _dragEdits = new();

    /// <summary>Les lignes des gizmos de cette image, à ajouter au rendu.</summary>
    public DrawContext GizmoDraw => _draw;

    /// <summary>
    /// Dessine les gizmos des extensions pour la sélection, et gère leurs poignées. Rend vrai si la souris est prise
    /// par une poignée (le viewport ne doit alors ni tourner ni sélectionner).
    /// </summary>
    public bool DrawGizmos(WorldView view, bool hovered)
    {
        _draw.Clear();
        _view = view;
        _hovered = hovered;
        _consumed = _drag is not null;
        _draw.CameraPosition = view.EyePosition();
        _draw.PixelsPerRadian = view.PixelsPerRadian;

        if (_registry is not null && _session.Play is null && _session.Selection is { } node)
            foreach (var gizmo in _registry.Gizmos)
                if (gizmo.NodeType.IsInstanceOfType(node))
                    Guard(gizmo, $"Gizmo de {gizmo.NodeType.Name}", () => gizmo.Draw(node, this));

        // Relâchement : les changements faits pendant le glissement deviennent une seule commande.
        if (_drag is not null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            EndDrag();

        // Étiquettes des gizmos, en surimpression.
        var dl = ImGui.GetWindowDrawList();
        foreach (var label in _draw.Labels)
            if (view.Project(label.At, out var s))
                dl.AddText(view.ImageMin + s + new float2(8, -8), EditorTheme.Col(label.Color), label.Text);
        return _consumed;
    }

    public DrawContext Draw => _draw;

    public bool Handle(string id, float3 position, float4 color, out float3 dragged)
    {
        dragged = position;
        if (_view is null || !_view.Project(position, out var screen))
            return false;
        var mouse = ImGui.GetIO().MousePos - _view.ImageMin;
        bool active = _drag == id;
        bool hot = active || _drag is null && _hovered && float2.Distance(mouse, screen) < 9;

        var dl = ImGui.GetWindowDrawList();
        var at = _view.ImageMin + screen;
        dl.AddCircleFilled(at, hot ? 7 : 5, EditorTheme.Col(color));
        dl.AddCircle(at, hot ? 8 : 6, EditorTheme.Col(new float4(0, 0, 0, 0.8f)));

        if (!active && hot && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _drag = id;
            _dragPlanePoint = position;
            _dragPlaneNormal = float3.Normalize(_view.Target - _view.EyePosition());
            _consumed = true;
            return true;
        }
        if (!active)
            return false;

        _consumed = true;
        if (_view.Ray(mouse, out var origin, out var dir))
        {
            float denom = float3.Dot(dir, _dragPlaneNormal);
            if (MathF.Abs(denom) > 1e-6f)
            {
                float t = float3.Dot(_dragPlanePoint - origin, _dragPlaneNormal) / denom;
                if (t > 0)
                    dragged = origin + dir * t;
            }
        }
        return true;
    }

    void EndDrag()
    {
        var commands = new List<IEditorCommand>();
        foreach (var ((node, property), original) in _dragEdits)
        {
            var final = property.GetValue(node);
            property.SetValue(node, original);
            if (!Equals(final, original))
                commands.Add(new SetPropertyCommand(node, property, final));
        }
        _dragEdits.Clear();
        _drag = null;
        if (commands.Count > 0)
            _session.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Gizmo", commands));
    }

    // ----- IEditorContext ----------------------------------------------------------------------------

    public Node Scene => _session.Scene;

    public Node? Selection
    {
        get => _session.Selection;
        set => _session.Selection = value;
    }

    public string ContentRoot => _session.Serializer.ContentRoot ?? "";

    public World? Preview => _session.Preview;

    public World? Game => _session.Play?.World;

    List<IEditorCommand>? _batch;

    void Execute(IEditorCommand command)
    {
        if (_batch is not null)
        {
            command.Do();
            _batch.Add(command);
        }
        else
        {
            _session.History.Execute(command);
        }
    }

    public void Set(Node node, string property, object? value)
    {
        var p = node.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"{node.GetType().Name} n'a pas de propriété {property}");
        if (_drag is not null)
        {
            // Glissement d'une poignée : en direct, et une seule commande au relâchement.
            _dragEdits.TryAdd((node, p), p.GetValue(node));
            p.SetValue(node, value);
            _session.Touch();
            return;
        }
        Execute(new SetPropertyCommand(node, p, value));
    }

    public void Rename(Node node, string name) => Execute(new RenameCommand(node, name));

    public void Add(Node parent, Node child)
    {
        if (!parent.HasEditableChildren)
            throw new InvalidOperationException($"{parent.Name} vient d'une scène instanciée : on n'y ajoute pas d'enfant.");
        Execute(new AddNodeCommand(parent, child));
    }

    public void Remove(Node node)
    {
        if (node.Parent is not { HasEditableChildren: true })
            throw new InvalidOperationException($"{node.Name} ne se supprime pas (racine, ou nœud d'une scène instanciée).");
        Execute(new RemoveNodeCommand(node));
    }

    public void Group(string label, Action edits)
    {
        if (_batch is not null)
        {
            edits();
            return;
        }
        _batch = new List<IEditorCommand>();
        try
        {
            edits();
        }
        finally
        {
            var batch = _batch;
            _batch = null;
            // Les commandes sont déjà faites : on les annule puis on les rejoue d'un bloc, dans l'historique.
            for (int i = batch.Count - 1; i >= 0; i--)
                batch[i].Undo();
            if (batch.Count > 0)
                _session.History.Execute(new CompositeCommand(label, batch));
        }
    }

    public void PropertyField(Node node, string property)
    {
        var p = SceneSerializer.EditableProperties(node.GetType()).FirstOrDefault(x => x.Name == property)
            ?? throw new ArgumentException($"{node.GetType().Name}.{property} n'est pas une propriété éditable");
        _properties.DrawPropertyRow(node, p, _session.InstanceBaseline(node) is { } b && b.GetType() == node.GetType() ? b : null);
    }

    public bool View3D(string id, World world, float2 size, DrawContext? overlay = null) =>
        PreviewView?.Draw(id, world, size, overlay) ?? false;

    public void RequestReload() => ReloadRequested = true;

    Dictionary<string, string>? _settings;

    string SettingsPath => Path.Combine(_session.ProjectRoot, ".poiesis", "extensions.json");

    public string? GetSetting(string key)
    {
        LoadSettings();
        return _settings!.GetValueOrDefault(key);
    }

    public void SetSetting(string key, string? value)
    {
        LoadSettings();
        if (value is null ? !_settings!.Remove(key) : _settings!.TryGetValue(key, out var old) && old == value)
            return;
        if (value is not null)
            _settings![key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in _settings!.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                json[k] = v;
            File.WriteAllText(SettingsPath, SceneSerializer.ToJsonString(json) + "\n");
        }
        catch (IOException ex)
        {
            _log.Warn("extension", $"Réglages non enregistrés : {ex.Message}");
        }
    }

    void LoadSettings()
    {
        if (_settings is not null)
            return;
        _settings = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(SettingsPath) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath)) is System.Text.Json.Nodes.JsonObject obj)
                foreach (var (k, v) in obj)
                    if (v is not null)
                        _settings[k] = v.ToString();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _log.Warn("extension", $"Réglages illisibles : {ex.Message}");
        }
    }

    public void Log(string message) => _log.Info("extension", message);

    public void Warn(string message) => _log.Warn("extension", message);
}
