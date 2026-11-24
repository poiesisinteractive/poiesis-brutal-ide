using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>L'arbre de la scène : sélection, menu contextuel (ajouter, dupliquer, supprimer), glisser-déposer.</summary>
sealed class ScenePanel(EditorSession session)
{
    Node? _pendingDelete;
    (Node Node, Node NewParent)? _pendingMove;

    public void Draw()
    {
        if (ImGui.Begin("Scène"u8))
        {
            if (session.Modules.Module is null)
                ImGui.TextDisabled("Compilez le jeu pour ajouter des nœuds."u8);
            DrawNode(session.Scene);

            // Clic dans le vide : désélection.
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
                session.Selection = null;
        }
        ImGui.End();

        // Les modifications de l'arbre se font hors du parcours.
        if (_pendingDelete is { } del)
        {
            if (session.Selection is { } sel && del.IsSelfOrAncestorOf(sel))
                session.Selection = null;
            session.History.Execute(new RemoveNodeCommand(del));
            _pendingDelete = null;
        }
        if (_pendingMove is { } move)
        {
            session.History.Execute(new ReparentCommand(move.Node, move.NewParent));
            _pendingMove = null;
        }
    }

    void DrawNode(Node node)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        if (node.Children.Count == 0)
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (node == session.Selection)
            flags |= ImGuiTreeNodeFlags.Selected;

        var typeName = session.NodeTypes.All.FirstOrDefault(t => t.Type == node.GetType()).Name ?? node.GetType().Name;
        var label = node.Name.Length > 0 ? node.Name : $"({typeName})";
        bool open = ImGui.TreeNodeEx(node.Id.ToString(), flags, label);

        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            session.Selection = node;
        if (ImGui.IsItemHovered())
            ImGui.SetItemTooltip(typeName);

        DragDrop(node);
        ContextMenu(node);

        if (open)
        {
            foreach (var child in node.Children.ToArray())
                DrawNode(child);
            ImGui.TreePop();
        }
    }

    unsafe void DragDrop(Node node)
    {
        if (node.Parent is not null && ImGui.BeginDragDropSource())
        {
            var id = node.Id;
            ImGui.SetDragDropPayload("poiesis-node"u8, (void*)&id, (nuint)sizeof(Guid));
            ImGui.Text(node.Name);
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("poiesis-node"u8);
            if (!payload.IsNull())
            {
                var id = *(Guid*)payload.Data;
                if (session.FindNode(id) is { } dragged && !dragged.IsSelfOrAncestorOf(node))
                    _pendingMove = (dragged, node);
            }
            ImGui.EndDragDropTarget();
        }
    }

    void ContextMenu(Node node)
    {
        if (!ImGui.BeginPopupContextItem())
            return;
        session.Selection = node;
        if (ImGui.BeginMenu("Ajouter un enfant"u8))
        {
            foreach (var (name, type) in session.NodeTypes.All.Where(t => t.Type != typeof(SceneRoot)).OrderBy(t => t.Name))
                if (ImGui.MenuItem(name))
                {
                    var child = (Node)Activator.CreateInstance(type)!;
                    child.Name = Widgets.Pretty(type.Name.Replace("Node", ""));
                    session.History.Execute(new AddNodeCommand(node, child));
                    session.Selection = child;
                }
            ImGui.EndMenu();
        }
        if (node.Parent is { } parent)
        {
            if (ImGui.MenuItem("Dupliquer"u8, "Ctrl+D"u8))
            {
                var copy = session.CloneNode(node);
                session.History.Execute(new AddNodeCommand(parent, copy));
                session.Selection = copy;
            }
            if (ImGui.MenuItem("Supprimer"u8, "Suppr"u8))
                _pendingDelete = node;
        }
        ImGui.EndPopup();
    }

    /// <summary>Raccourcis clavier qui agissent sur la sélection (appelés hors de toute fenêtre).</summary>
    public void Shortcuts()
    {
        if (ImGui.GetIO().WantTextInput || session.Selection is not { Parent: { } parent } node || session.Play is not null)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
            _pendingDelete = node;
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.D, ImGuiInputFlags.RouteGlobal))
        {
            var copy = session.CloneNode(node);
            session.History.Execute(new AddNodeCommand(parent, copy));
            session.Selection = copy;
        }
    }
}

/// <summary>Les propriétés du nœud sélectionné.</summary>
sealed class InspectorPanel(EditorSession session, PropertyEditor editor)
{
    public void Draw()
    {
        if (ImGui.Begin("Inspecteur"u8))
        {
            if (session.Selection is { } node)
            {
                var typeName = session.NodeTypes.All.FirstOrDefault(t => t.Type == node.GetType()).Name ?? node.GetType().Name;
                ImGui.TextColored(EditorTheme.Accent, typeName);
                ImGui.SameLine();
                ImGui.TextDisabled(node.Id.ToString()[..8]);
                if (session.Play is not null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
                    ImGui.TextWrapped("En jeu : les modifications s'appliqueront à la prochaine partie."u8);
                    ImGui.PopStyleColor();
                }
                ImGui.Spacing();
                editor.Draw(node);
            }
            else
            {
                ImGui.TextDisabled("Rien de sélectionné."u8);
            }
        }
        ImGui.End();
    }
}

/// <summary>Le journal de l'éditeur : build, hot reload, agent IA. Filtrable par niveau.</summary>
sealed class ConsolePanel(LogBuffer log)
{
    bool _showInfo = true, _showWarnings = true, _showErrors = true;
    int _lastVersion = -1;

    public void Draw()
    {
        if (ImGui.Begin("Console"u8))
        {
            ImGui.Checkbox("Infos"u8, ref _showInfo);
            ImGui.SameLine();
            ImGui.Checkbox("Avertissements"u8, ref _showWarnings);
            ImGui.SameLine();
            ImGui.Checkbox("Erreurs"u8, ref _showErrors);
            ImGui.SameLine();
            if (ImGui.Button("Effacer"u8))
                log.Clear();
            ImGui.Separator();

            if (ImGui.BeginChild("lines"u8, new float2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            {
                foreach (var e in log.Snapshot())
                {
                    if (e.Level == LogLevel.Info && !_showInfo || e.Level == LogLevel.Warning && !_showWarnings || e.Level == LogLevel.Error && !_showErrors)
                        continue;
                    var color = e.Level switch
                    {
                        LogLevel.Error => EditorTheme.Error,
                        LogLevel.Warning => EditorTheme.Warning,
                        _ => new float4(0.85f, 0.85f, 0.88f, 1),
                    };
                    ImGui.TextDisabled($"{e.Time:HH:mm:ss} [{e.Source}]");
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    ImGui.Text(e.Text);
                    ImGui.PopStyleColor();
                }
                // Défilement automatique quand une ligne arrive et qu'on est déjà en bas.
                if (_lastVersion != log.Version && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                    ImGui.SetScrollHereY(1f);
                _lastVersion = log.Version;
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }
}

/// <summary>
/// Les fichiers de données du jeu (<c>content/**/*.json</c> hors scènes) : définitions de vaisseaux et d'armes.
/// Édition directe des champs ; à l'enregistrement, le module est rechargé pour que l'aperçu et le jeu en tiennent compte.
/// </summary>
sealed class DataPanel(ProjectConfig project, LogBuffer log, Action reloadModule)
{
    string? _file;
    JsonObject? _doc;
    bool _dirty;
    string? _error;

    public void Draw()
    {
        if (ImGui.Begin("Données"u8))
        {
            float listWidth = 220;
            if (ImGui.BeginChild("files"u8, new float2(listWidth, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX))
            {
                if (Directory.Exists(project.ContentRoot))
                    foreach (var dir in Directory.EnumerateDirectories(project.ContentRoot).Order(StringComparer.Ordinal))
                    {
                        var files = Directory.EnumerateFiles(dir, "*.json").Where(f => !f.EndsWith(".scene.json")).Order(StringComparer.Ordinal).ToArray();
                        if (files.Length == 0)
                            continue;
                        if (ImGui.TreeNodeEx(Path.GetFileName(dir), ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            foreach (var f in files)
                                if (ImGui.Selectable(Path.GetFileName(f), f == _file))
                                    Open(f);
                            ImGui.TreePop();
                        }
                    }
            }
            ImGui.EndChild();
            ImGui.SameLine();
            if (ImGui.BeginChild("fields"u8))
            {
                if (_error is not null)
                    ImGui.TextColored(EditorTheme.Error, _error);
                if (_doc is not null)
                {
                    ImGui.TextColored(EditorTheme.Accent, Path.GetRelativePath(project.Root, _file!));
                    ImGui.Separator();
                    foreach (var (key, value) in _doc.ToArray())
                        DrawField(key, value);
                    ImGui.Spacing();
                    ImGui.BeginDisabled(!_dirty);
                    if (ImGui.Button("Enregistrer et recharger"u8))
                        Save();
                    ImGui.EndDisabled();
                    if (_dirty)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Annuler les modifications"u8))
                            Open(_file!);
                    }
                }
                else if (_error is null)
                {
                    ImGui.TextDisabled("Choisissez un fichier."u8);
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    void DrawField(string key, JsonNode? value)
    {
        ImGui.PushID(key);
        var label = Widgets.Pretty(key);
        switch (value?.GetValueKind())
        {
            case JsonValueKind.Number:
                var f = value.GetValue<double>();
                var v = (float)f;
                if (ImGui.DragFloat(label, ref v, MathF.Max(MathF.Abs(v) * 0.005f, 0.01f), 0, 0, "%.2f"u8))
                {
                    _doc![key] = Math.Abs(v - MathF.Round(v)) < 1e-4f ? JsonValue.Create((long)MathF.Round(v)) : JsonValue.Create(Math.Round(v, 3));
                    _dirty = true;
                }
                break;
            case JsonValueKind.String:
                var s = value.GetValue<string>();
                if (key == "id")
                {
                    ImGui.LabelText(label, s);
                    break;
                }
                if (Widgets.TextField(label, $"data-{_file}-{key}", ref s) && s != value.GetValue<string>())
                {
                    _doc![key] = s;
                    _dirty = true;
                }
                break;
            case JsonValueKind.True or JsonValueKind.False:
                var b = value.GetValue<bool>();
                if (ImGui.Checkbox(label, ref b))
                {
                    _doc![key] = b;
                    _dirty = true;
                }
                break;
            default:
                ImGui.LabelText(label, value?.ToJsonString() ?? "null");
                break;
        }
        ImGui.PopID();
    }

    void Open(string file)
    {
        _file = file;
        _dirty = false;
        try
        {
            _doc = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
            _error = null;
        }
        catch (JsonException ex)
        {
            _doc = null;
            _error = $"JSON invalide : {ex.Message}";
        }
    }

    void Save()
    {
        File.WriteAllText(_file!, _doc!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        _dirty = false;
        log.Info("données", $"Enregistré : {Path.GetRelativePath(project.Root, _file!)}");
        reloadModule();
    }
}
