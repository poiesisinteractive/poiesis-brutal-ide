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

    public string? Selected => _selected;

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
            ImGui.TextDisabled(folder.Length == 0 ? "dans content/" : $"dans {folder}/");
            ImGui.Separator();

            if (ImGui.BeginChild("tree"u8))
            {
                foreach (var child in _tree.Children)
                    DrawEntry(child);
                // Déposer sur le fond : à la racine de content/.
                ImGui.Dummy(new float2(ImGui.GetContentRegionAvail().X, MathF.Max(ImGui.GetContentRegionAvail().Y, 20)));
                DropTarget("");
            }
            ImGui.EndChild();
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
            ImGui.SetItemTooltip(entry.Kind switch
            {
                ContentKind.Scene => $"{entry.Path}\nDouble-clic : ouvrir. Glisser dans l'arbre de la scène : instancier.",
                ContentKind.Data => $"{entry.Path}\nDouble-clic : éditer dans le panneau Données.",
                _ => entry.Path,
            });

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
