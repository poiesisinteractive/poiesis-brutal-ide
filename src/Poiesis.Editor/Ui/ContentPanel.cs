using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>
/// Le navigateur de contenu : l'arborescence de <c>content/</c>. Double-clic : ouvrir (scène, ou données dans le panneau
/// Données). Glisser une scène dans l'arbre de la scène l'instancie ; glisser un fichier ou un dossier sur un dossier le
/// déplace, et les scènes qui l'instancient sont mises à jour.
/// </summary>
sealed class ContentPanel(ContentLibrary library, EditorSession session, LogBuffer log, Action<string> openScene, Action<string> openData)
{
    ContentEntry? _tree;
    DateTime _scannedAt;
    string? _selected;

    // Actions demandées pendant le parcours, faites après.
    (string From, string To)? _pendingMove;
    string? _renaming;
    string _renameText = "";
    string? _deleting;
    IReadOnlyList<string> _deleteReferences = [];
    string? _creatingIn;
    bool _creatingScene;
    string _createName = "";
    int _createRootType;

    public void Refresh() => _tree = null;

    /// <summary>Les vignettes (fournies par l'éditeur).</summary>
    public ThumbnailService? Thumbnails { get; set; }

    public string? Selected => _selected;

    /// <summary>Sélectionne un fichier, ou un dossier (terminé par « / »), par son chemin dans <c>content/</c>.</summary>
    public void Select(string? path) => _selected = path;

    public void Draw()
    {
        if (_tree is null || DateTime.UtcNow - _scannedAt > TimeSpan.FromSeconds(1))
        {
            _tree = library.Tree();
            _scannedAt = DateTime.UtcNow;
        }

        if (ImGui.Begin("Contenu"u8))
        {
            var folder = SelectedFolder();
            if (ImGui.SmallButton("+ Dossier"u8))
                StartCreate(folder, scene: false);
            ImGui.SameLine();
            ImGui.BeginDisabled(session.Modules.Module is null);
            if (ImGui.SmallButton("+ Scène"u8))
                StartCreate(folder, scene: true);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton(Grid ? "Arbre" : "Vignettes"))
                Grid = !Grid;
            ImGui.SameLine();
            ImGui.TextDisabled(folder.Length == 0 ? "dans content/" : $"dans {folder}/");
            ImGui.Separator();

            // Sous l'arbre, l'aperçu du fichier sélectionné.
            var preview = _selected is not null && !Grid ? Thumbnails?.Get(_selected) : null;
            float previewHeight = preview is null ? 0 : 150;
            if (ImGui.BeginChild("tree"u8, new float2(0, -previewHeight)))
            {
                if (Grid)
                    DrawGrid(folder);
                else
                    foreach (var child in _tree.Children)
                        DrawEntry(child);
                // Déposer sur le fond : à la racine de content/ (ou dans le dossier affiché, en vignettes).
                ImGui.Dummy(new float2(ImGui.GetContentRegionAvail().X, MathF.Max(ImGui.GetContentRegionAvail().Y, 20)));
                DropTarget(Grid ? folder : "");
            }
            ImGui.EndChild();
            if (preview is { } image)
            {
                ImGui.Image(image, new float2(140, 140));
                ImGui.SameLine();
                ImGui.TextDisabled(Path.GetFileName(_selected!));
            }
            Popups();
        }
        ImGui.End();

        if (_pendingMove is { } move)
        {
            _pendingMove = null;
            Move(move.From, move.To);
        }
    }

    string SelectedFolder()
    {
        if (_selected is null)
            return "";
        if (_selected.EndsWith('/'))
            return _selected.TrimEnd('/');
        return Directory.Exists(library.Abs(_selected)) ? _selected : Path.GetDirectoryName(_selected)?.Replace('\\', '/') ?? "";
    }

    void DrawEntry(ContentEntry entry)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (entry.Kind != ContentKind.Folder)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (entry.Path == _selected)
            flags |= ImGuiTreeNodeFlags.Selected;

        var color = entry.Kind switch
        {
            ContentKind.Scene => EditorTheme.Accent,
            ContentKind.Data => new float4(0.85f, 0.85f, 0.88f, 1),
            ContentKind.Folder => new float4(0.95f, 0.82f, 0.45f, 1),
            _ => EditorTheme.Muted,
        };
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        bool open = ImGui.TreeNodeEx(entry.Path, flags, entry.Name);
        ImGui.PopStyleColor();

        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            _selected = entry.Path;
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Open(entry);
        if (ImGui.IsItemHovered())
            Tooltip(entry);

        ScenePanel.ContentDragSource(entry.Path);
        if (entry.Kind == ContentKind.Folder)
            DropTarget(entry.Path);
        ContextMenu(entry);

        if (open && entry.Kind == ContentKind.Folder)
        {
            foreach (var child in entry.Children)
                DrawEntry(child);
            ImGui.TreePop();
        }
    }

    /// <summary>Vue en vignettes : le contenu d'un dossier en tuiles, double-clic pour ouvrir ou entrer.</summary>
    public bool Grid { get; set; }

    const float Tile = 96;

    void DrawGrid(string folder)
    {
        var entry = Find(_tree!, folder) ?? _tree!;
        int columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + 8) / (Tile + 8)));
        var items = new List<ContentEntry>();
        if (entry.Path.Length > 0)
            items.Add(new ContentEntry(Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "", "..", ContentKind.Folder, []));
        items.AddRange(entry.Children);
        if (ImGui.BeginTable("grid"u8, columns))
        {
            foreach (var item in items)
            {
                ImGui.TableNextColumn();
                ImGui.PushID(item.Path + item.Name);
                DrawTile(item);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    void DrawTile(ContentEntry entry)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var size = new float2(Tile, Tile);
        bool selected = entry.Path == _selected && entry.Name != "..";
        ImGui.InvisibleButton("tile"u8, size + new float2(0, ImGui.GetTextLineHeight() + 4));
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
            _selected = entry.Path;
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (entry.Kind == ContentKind.Folder)
                _selected = entry.Path.Length == 0 ? null : entry.Path + "/";
            else
                Open(entry);
        }
        if (entry.Name != "..")
        {
            ScenePanel.ContentDragSource(entry.Path);
            if (hovered)
                Tooltip(entry);
            ContextMenu(entry);
        }
        if (entry.Kind == ContentKind.Folder)
            DropTarget(entry.Path);

        var background = selected ? EditorTheme.Accent * new float4(1, 1, 1, 0.35f) : hovered ? new float4(1, 1, 1, 0.08f) : new float4(1, 1, 1, 0.03f);
        dl.AddRectFilled(min, min + size, EditorTheme.Col(background), 4);
        if (entry.Kind != ContentKind.Folder && Thumbnails?.Get(entry.Path) is { } thumb)
            dl.AddImage(thumb, min + new float2(4), min + size - new float2(4));
        else
        {
            var (label, color) = entry.Kind switch
            {
                ContentKind.Folder => (entry.Name == ".." ? ".." : "dossier", new float4(0.95f, 0.82f, 0.45f, 1)),
                ContentKind.Scene => ("scène", EditorTheme.Accent),
                ContentKind.Data => ("données", new float4(0.85f, 0.85f, 0.88f, 1)),
                _ => (Path.GetExtension(entry.Name).TrimStart('.'), EditorTheme.Muted),
            };
            var textSize = ImGui.CalcTextSize(label);
            dl.AddText(min + (size - textSize) * 0.5f, EditorTheme.Col(color), label);
        }
        var name = entry.Name.Length > 14 ? entry.Name[..13] + "…" : entry.Name;
        dl.AddText(min + new float2(2, Tile + 2), EditorTheme.Col(new float4(0.85f, 0.85f, 0.88f, 1)), name);
    }

    static ContentEntry? Find(ContentEntry root, string path)
    {
        path = path.TrimEnd('/');
        if (root.Path == path)
            return root;
        foreach (var child in root.Children)
            if (child.Kind == ContentKind.Folder && (path == child.Path || path.StartsWith(child.Path + "/", StringComparison.Ordinal)))
                return Find(child, path);
        return null;
    }

    /// <summary>La bulle d'un fichier : sa vignette s'il en a une, et ce qu'on peut en faire.</summary>
    void Tooltip(ContentEntry entry)
    {
        if (!ImGui.BeginTooltip())
            return;
        if (entry.Kind != ContentKind.Folder && Thumbnails?.Get(entry.Path) is { } thumb)
            ImGui.Image(thumb, new float2(ThumbnailService.Size, ThumbnailService.Size));
        ImGui.Text(entry.Path);
        ImGui.TextDisabled(entry.Kind switch
        {
            ContentKind.Scene => "Double-clic : ouvrir. Glisser dans l'arbre de la scène : instancier.",
            ContentKind.Data => "Double-clic : éditer dans le panneau Données.",
            ContentKind.Folder => "Glisser un fichier dessus pour l'y déplacer.",
            _ => "",
        });
        ImGui.EndTooltip();
    }

    void DropTarget(string folder)
    {
        if (!ImGui.BeginDragDropTarget())
            return;
        if (ScenePanel.ReadContentPayload() is { } dragged)
        {
            var to = folder.Length == 0 ? Path.GetFileName(dragged) : $"{folder}/{Path.GetFileName(dragged)}";
            if (to != dragged)
                _pendingMove = (dragged, to);
        }
        ImGui.EndDragDropTarget();
    }

    void Open(ContentEntry entry)
    {
        switch (entry.Kind)
        {
            case ContentKind.Scene:
                openScene(library.Abs(entry.Path));
                break;
            case ContentKind.Data:
                openData(library.Abs(entry.Path));
                break;
        }
    }

    void ContextMenu(ContentEntry entry)
    {
        if (!ImGui.BeginPopupContextItem())
            return;
        _selected = entry.Path;
        if (entry.Kind == ContentKind.Folder)
        {
            if (ImGui.MenuItem("Nouveau dossier"u8))
                StartCreate(entry.Path, scene: false);
            if (ImGui.MenuItem("Nouvelle scène"u8, ""u8, false, session.Modules.Module is not null))
                StartCreate(entry.Path, scene: true);
            ImGui.Separator();
        }
        else
        {
            if (entry.Kind is ContentKind.Scene or ContentKind.Data && ImGui.MenuItem("Ouvrir"u8))
                Open(entry);
            if (entry.Kind == ContentKind.Scene)
            {
                var target = session.Selection is { HasEditableChildren: true } s ? s : session.Scene;
                var targetName = target.Name.Length > 0 ? target.Name : "la scène";
                if (ImGui.MenuItem($"Instancier dans « {targetName} »", ""u8, false, session.Modules.Module is not null && target.HasEditableChildren))
                    Try(() => session.InstantiateScene(entry.Path, target), "Instancier");
            }
            if (ImGui.MenuItem("Dupliquer"u8))
                Try(() => _selected = library.Duplicate(entry.Path), "Dupliquer");
        }
        if (ImGui.MenuItem("Renommer"u8))
        {
            _renaming = entry.Path;
            _renameText = entry.Name;
        }
        if (ImGui.MenuItem("Références"u8))
        {
            var refs = library.FindReferences(entry.Path);
            log.Info("contenu", refs.Count == 0 ? $"{entry.Path} : instancié nulle part." : $"{entry.Path} est instancié par : {string.Join(", ", refs)}");
        }
        if (ImGui.MenuItem("Supprimer"u8))
        {
            _deleting = entry.Path;
            _deleteReferences = library.FindReferences(entry.Path);
        }
        ImGui.EndPopup();
    }

    void StartCreate(string folder, bool scene)
    {
        _creatingIn = folder;
        _creatingScene = scene;
        _createName = scene ? "nouvelle-scene" : "nouveau-dossier";
    }

    void Popups()
    {
        Modal("Renommer", _renaming is not null, () =>
        {
            ImGui.Text($"Renommer {_renaming ?? ""}");
            ImGui.SetNextItemWidth(360);
            Widgets.TextField("Nom", "content-rename", ref _renameText);
            ImGui.TextDisabled("Les scènes qui l'instancient seront mises à jour."u8);
            if (ImGui.Button("Renommer"u8))
            {
                var from = _renaming!;
                var dir = Path.GetDirectoryName(from)?.Replace('\\', '/') ?? "";
                var to = dir.Length == 0 ? _renameText : $"{dir}/{_renameText}";
                _renaming = null;
                if (to != from)
                    _pendingMove = (from, to);
                return true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Annuler"u8))
            {
                _renaming = null;
                return true;
            }
            return false;
        });

        Modal("Supprimer", _deleting is not null, () =>
        {
            ImGui.Text($"Supprimer {_deleting ?? ""} ?");
            if (_deleteReferences.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
                ImGui.Text($"Instancié par {_deleteReferences.Count} scène(s), qui ne s'ouvriront plus :");
                ImGui.PopStyleColor();
                foreach (var r in _deleteReferences)
                    ImGui.BulletText(r);
            }
            if (ImGui.Button("Supprimer"u8))
            {
                var path = _deleting!;
                _deleting = null;
                Try(() =>
                {
                    library.Delete(path);
                    log.Info("contenu", $"Supprimé : {path}");
                    if (_selected == path)
                        _selected = null;
                    Refresh();
                }, "Supprimer");
                return true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Annuler"u8))
            {
                _deleting = null;
                return true;
            }
            return false;
        });

        Modal(_creatingScene ? "Nouvelle scène" : "Nouveau dossier", _creatingIn is not null, () =>
        {
            ImGui.Text($"Dans {(_creatingIn!.Length == 0 ? "content" : _creatingIn)}/");
            ImGui.SetNextItemWidth(360);
            Widgets.TextField("Nom", "content-create", ref _createName);
            var types = session.NodeTypes.All.OrderBy(t => t.Type == typeof(SceneRoot) ? "" : t.Name).ToList();
            if (_creatingScene)
            {
                _createRootType = Math.Clamp(_createRootType, 0, types.Count - 1);
                if (ImGui.BeginCombo("Racine"u8, types[_createRootType].Name))
                {
                    for (int i = 0; i < types.Count; i++)
                        if (ImGui.Selectable(types[i].Name, i == _createRootType))
                            _createRootType = i;
                    ImGui.EndCombo();
                }
            }
            if (ImGui.Button("Créer"u8))
            {
                var folder = _creatingIn!;
                _creatingIn = null;
                Try(() =>
                {
                    if (_creatingScene)
                    {
                        var root = (Node)Activator.CreateInstance(types[_createRootType].Type)!;
                        root.Name = Widgets.Pretty(_createName.Replace('-', ' '));
                        _selected = library.CreateScene(folder, _createName, session.Serializer.Write(root));
                        log.Info("contenu", $"Scène créée : {_selected}");
                        openScene(library.Abs(_selected));
                    }
                    else
                    {
                        _selected = library.CreateFolder(folder, _createName);
                    }
                    Refresh();
                }, "Créer");
                return true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Annuler"u8))
            {
                _creatingIn = null;
                return true;
            }
            return false;
        });
    }

    /// <summary>Une fenêtre modale ouverte tant que <paramref name="wanted"/> ; <paramref name="body"/> rend vrai pour la fermer.</summary>
    static void Modal(string title, bool wanted, Func<bool> body)
    {
        if (wanted && !ImGui.IsPopupOpen(title))
            ImGui.OpenPopup(title);
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (body())
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Déplace (ou renomme) et met à jour les références, sur disque et dans la scène ouverte.</summary>
    public void Move(string from, string to)
    {
        Try(() =>
        {
            var changed = library.Move(from, to);
            session.RemapContent(from, to);
            if (_selected is { } sel && ContentLibrary.Remap(sel, from, to) is { } moved)
                _selected = moved;
            log.Info("contenu", changed.Count == 0
                ? $"Déplacé : {from} → {to}"
                : $"Déplacé : {from} → {to} ; références mises à jour dans {string.Join(", ", changed)}");
            Refresh();
        }, "Déplacer");
    }

    void Try(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Error("contenu", $"{what} : {ex.Message}");
        }
    }
}
