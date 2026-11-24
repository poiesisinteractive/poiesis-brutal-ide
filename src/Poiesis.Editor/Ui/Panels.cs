using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>
/// L'arbre de la scène : sélection, menu contextuel (ajouter, instancier, dupliquer, supprimer, enregistrer comme scène),
/// glisser-déposer (déplacer un nœud, ou déposer une scène du navigateur de contenu pour l'instancier), copier-coller.
/// Les nœuds venus d'une scène instanciée sont grisés : on change leurs propriétés, pas leur structure.
/// </summary>
sealed class ScenePanel(EditorSession session, ProjectConfig project, LogBuffer log, Action<string> openScene)
{
    Node? _pendingDelete;
    (Node Node, Node NewParent)? _pendingMove;
    (string Path, Node Parent)? _pendingInstance;
    Node? _saveAsScene;
    string _saveAsPath = "";

    public const string ContentPayload = "poiesis-content";

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
            SaveAsScenePopup();
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
        if (_pendingInstance is { } inst)
        {
            _pendingInstance = null;
            Try(() => session.InstantiateScene(inst.Path, inst.Parent), "Instancier");
        }
    }

    void Try(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Error("scène", $"{what} : {ex.Message}");
        }
    }

    string TypeName(Node node) => session.NodeTypes.All.FirstOrDefault(t => t.Type == node.GetType()).Name ?? node.GetType().Name;

    void DrawNode(Node node)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        // Les instances sont repliées par défaut : leur contenu vient d'ailleurs.
        if (node.InstanceOf is null)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (node.Children.Count == 0)
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (node == session.Selection)
            flags |= ImGuiTreeNodeFlags.Selected;

        var typeName = TypeName(node);
        var label = node.Name.Length > 0 ? node.Name : $"({typeName})";
        bool inInstance = node.OwningInstance is not null;
        if (node.InstanceOf is not null)
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Accent);
        else if (inInstance)
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Muted);
        bool open = ImGui.TreeNodeEx(node.Id.ToString(), flags, label);
        if (node.InstanceOf is not null || inInstance)
            ImGui.PopStyleColor();
        if (node.InstanceOf is { } path)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({Path.GetFileName(path).Replace(".scene.json", "")})");
        }

        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            session.Selection = node;
        if (ImGui.IsItemHovered())
            ImGui.SetItemTooltip(node.InstanceOf is { } p ? $"{typeName} · instance de {p}"
                : inInstance ? $"{typeName} · vient de l'instance « {node.OwningInstance!.Name} »" : typeName);

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
        if (node.Parent is { HasEditableChildren: true } && ImGui.BeginDragDropSource())
        {
            var id = node.Id;
            ImGui.SetDragDropPayload("poiesis-node"u8, (void*)&id, (nuint)sizeof(Guid));
            ImGui.Text(node.Name);
            ImGui.EndDragDropSource();
        }
        if (node.HasEditableChildren && ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("poiesis-node"u8);
            if (!payload.IsNull())
            {
                var id = *(Guid*)payload.Data;
                if (session.FindNode(id) is { } dragged && !dragged.IsSelfOrAncestorOf(node))
                    _pendingMove = (dragged, node);
            }
            // Une scène venue du navigateur de contenu : on l'instancie ici.
            if (ReadContentPayload() is { } path && path.EndsWith(".scene.json", StringComparison.Ordinal))
                _pendingInstance = (path, node);
            ImGui.EndDragDropTarget();
        }
    }

    /// <summary>Le chemin (relatif à <c>content/</c>) déposé depuis le navigateur de contenu, ou <c>null</c>.</summary>
    public static unsafe string? ReadContentPayload()
    {
        var payload = ImGui.AcceptDragDropPayload(ContentPayload);
        if (payload.IsNull())
            return null;
        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)payload.Data);
    }

    /// <summary>Commence à glisser un fichier de contenu (chemin relatif à <c>content/</c>), à appeler juste après son élément.</summary>
    public static unsafe void ContentDragSource(string relativePath)
    {
        if (!ImGui.BeginDragDropSource())
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(relativePath + "\0");
        fixed (byte* p = bytes)
            ImGui.SetDragDropPayload(ContentPayload, p, (nuint)bytes.Length);
        ImGui.Text(Path.GetFileName(relativePath));
        ImGui.EndDragDropSource();
    }

    void ContextMenu(Node node)
    {
        if (!ImGui.BeginPopupContextItem())
            return;
        session.Selection = node;
        bool editableChildren = node.HasEditableChildren;
        bool editable = node.Parent is { HasEditableChildren: true };

        if (ImGui.BeginMenu("Ajouter un enfant"u8, editableChildren))
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
        if (ImGui.BeginMenu("Instancier une scène"u8, editableChildren))
        {
            var current = session.ScenePath is { } sp ? Path.GetFullPath(sp) : null;
            foreach (var f in Directory.EnumerateFiles(project.ContentRoot, "*.scene.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                if (Path.GetFullPath(f) != current && ImGui.MenuItem(session.Serializer.RelativePath(f)))
                    _pendingInstance = (session.Serializer.RelativePath(f), node);
            ImGui.EndMenu();
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Copier"u8, "Ctrl+C"u8))
            Copy(node);
        if (ImGui.MenuItem("Couper"u8, "Ctrl+X"u8, false, editable))
            Cut(node);
        if (ImGui.MenuItem("Coller"u8, "Ctrl+V"u8, false, EditorSession.IsNodeClipboard(Clipboard())))
            Paste(node);
        if (ImGui.MenuItem("Dupliquer"u8, "Ctrl+D"u8, false, editable))
            Duplicate(node);
        if (ImGui.MenuItem("Supprimer"u8, "Suppr"u8, false, editable))
            _pendingDelete = node;
        ImGui.Separator();
        if (ImGui.MenuItem("Enregistrer comme scène…"u8, ""u8, false, editable && node.InstanceOf is null))
        {
            _saveAsScene = node;
            var folder = session.ScenePath is { } scene ? Path.GetDirectoryName(session.Serializer.RelativePath(scene))!.Replace('\\', '/') : "scenes";
            _saveAsPath = $"{folder}/{Slug(node.Name)}.scene.json";
        }
        if (node.InstanceOf is { } path)
        {
            if (ImGui.MenuItem("Ouvrir la scène source"u8))
                openScene(Path.Combine(project.ContentRoot, path));
            if (ImGui.MenuItem("Rétablir toutes les surcharges"u8))
                RevertOverrides(session, node);
            if (ImGui.MenuItem("Rendre local"u8, ""u8, false, node.OwningInstance is null))
                session.History.Execute(new MakeLocalCommand(node));
        }
        ImGui.EndPopup();
    }

    static string Slug(string name)
    {
        var s = new string(name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--"))
            s = s.Replace("--", "-");
        s = s.Trim('-');
        return s.Length == 0 ? "branche" : s;
    }

    void SaveAsScenePopup()
    {
        if (_saveAsScene is not null && !ImGui.IsPopupOpen("Enregistrer comme scène"u8))
            ImGui.OpenPopup("Enregistrer comme scène"u8);
        if (!ImGui.BeginPopupModal("Enregistrer comme scène"u8, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.Text($"« {_saveAsScene?.Name ?? ""} » devient une scène réutilisable, et ce nœud une instance de cette scène.");
        ImGui.SetNextItemWidth(420);
        Widgets.TextField("Fichier (dans content/)", "save-branch-path", ref _saveAsPath);
        if (ImGui.Button("Enregistrer"u8) && _saveAsScene is { } node)
        {
            var path = _saveAsPath.EndsWith(".scene.json", StringComparison.Ordinal) ? _saveAsPath : _saveAsPath + ".scene.json";
            Try(() =>
            {
                session.SaveBranchAsScene(node, path);
                log.Info("scène", $"{node.Name} enregistré dans content/{path}, et remplacé par une instance.");
            }, "Enregistrer comme scène");
            _saveAsScene = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Annuler"u8))
        {
            _saveAsScene = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>Remet chaque propriété et chaque nom de l'instance à la valeur de sa scène source, en une commande.</summary>
    public static void RevertOverrides(EditorSession session, Node instance)
    {
        var commands = new List<IEditorCommand>();
        foreach (var n in instance.DescendantsAndSelf())
        {
            if (session.InstanceBaseline(n) is not { } b || b.GetType() != n.GetType())
                continue;
            if (n != instance && n.Name != b.Name)
                commands.Add(new RenameCommand(n, b.Name));
            foreach (var p in SceneSerializer.EditableProperties(n.GetType()))
                if (!PropertyEditor.SameValue(p, n, b))
                    commands.Add(new SetPropertyCommand(n, p, p.GetValue(b)));
        }
        if (commands.Count > 0)
            session.History.Execute(new CompositeCommand($"Rétablir les surcharges de {instance.Name}", commands));
    }

    static string? Clipboard()
    {
        try
        {
            return ImGui.GetClipboardText().ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    void Copy(Node node)
    {
        ImGui.SetClipboardText(session.CopyNode(node));
        log.Info("scène", $"Copié : {node.Name}");
    }

    void Cut(Node node)
    {
        ImGui.SetClipboardText(session.CopyNode(node));
        _pendingDelete = node;
    }

    void Paste(Node? target)
    {
        if (Clipboard() is { } text && EditorSession.IsNodeClipboard(text))
            Try(() => session.Paste(text, target), "Coller");
    }

    void Duplicate(Node node)
    {
        Try(() =>
        {
            var copy = session.CloneNode(node);
            session.History.Execute(new InsertNodeCommand(node.Parent!, node.Parent!.IndexOf(node) + 1, copy));
            session.Selection = copy;
        }, "Dupliquer");
    }

    /// <summary>Raccourcis clavier qui agissent sur la sélection (appelés hors de toute fenêtre).</summary>
    public void Shortcuts()
    {
        if (ImGui.GetIO().WantTextInput || session.Play is not null)
            return;
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.V, ImGuiInputFlags.RouteGlobal))
            Paste(session.Selection);
        if (session.Selection is not { } node)
            return;
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.C, ImGuiInputFlags.RouteGlobal))
            Copy(node);
        if (node.Parent is not { HasEditableChildren: true })
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
            _pendingDelete = node;
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.X, ImGuiInputFlags.RouteGlobal))
            Cut(node);
        if (ImGui.Shortcut(ImGuiKey.Mod_Ctrl | ImGuiKey.D, ImGuiInputFlags.RouteGlobal))
            Duplicate(node);
    }

    /// <summary>Pour les menus et les scripts : agit sur la sélection.</summary>
    public void CopySelection()
    {
        if (session.Selection is { } n)
            Copy(n);
    }

    public void CutSelection()
    {
        if (session.Selection is { Parent.HasEditableChildren: true } n)
            Cut(n);
    }

    public void PasteHere() => Paste(session.Selection);
}

/// <summary>Les propriétés du nœud sélectionné ; pour une instance, sa scène source et ses surcharges.</summary>
sealed class InspectorPanel(EditorSession session, PropertyEditor editor, Action<string> openScene, string contentRoot)
{
    /// <summary>Sections ajoutées par le jeu (API d'extension), dessinées sous les propriétés.</summary>
    public Action<Node>? Extensions { get; set; }

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
                InstanceHeader(node);
                ImGui.Spacing();
                editor.Draw(node);
                Extensions?.Invoke(node);
            }
            else
            {
                ImGui.TextDisabled("Rien de sélectionné."u8);
            }
        }
        ImGui.End();
    }

    void InstanceHeader(Node node)
    {
        if (node.InstanceOf is { } path)
        {
            ImGui.TextColored(EditorTheme.Accent, $"Instance de {path}");
            if (ImGui.SmallButton("Ouvrir la source"u8))
                openScene(Path.Combine(contentRoot, path));
            ImGui.SameLine();
            if (ImGui.SmallButton("Tout rétablir"u8))
                ScenePanel.RevertOverrides(session, node);
            ImGui.SameLine();
            ImGui.BeginDisabled(node.OwningInstance is not null);
            if (ImGui.SmallButton("Rendre local"u8))
                session.History.Execute(new MakeLocalCommand(node));
            ImGui.EndDisabled();
        }
        else if (node.OwningInstance is { } owner)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Muted);
            ImGui.TextWrapped($"Vient de l'instance « {owner.Name} » : vos modifications sont des surcharges, la scène source ne change pas.");
            ImGui.PopStyleColor();
        }
        if (session.InstanceBaseline(node) is not null)
            ImGui.TextDisabled("En couleur : surchargé. Clic droit sur une valeur pour la rétablir."u8);
    }
}

/// <summary>Le journal de l'éditeur : build, hot reload, agent IA. Filtrable par niveau.</summary>
sealed class ConsolePanel(LogBuffer log, CodeEditor code)
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
                    // Diagnostic ou test en échec : un clic ouvre l'emplacement dans l'éditeur de code.
                    if (e.Location is { } loc)
                    {
                        if (ImGui.IsItemHovered())
                        {
                            var min = ImGui.GetItemRectMin();
                            var max = ImGui.GetItemRectMax();
                            ImGui.GetWindowDrawList().AddLine(new float2(min.X, max.Y), max, Widgets.Col(color));
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                            ImGui.SetItemTooltip($"Ouvrir {Path.GetFileName(loc.File)}:{loc.Line} dans {code.Command}");
                        }
                        if (ImGui.IsItemClicked())
                            code.Open(loc);
                    }
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

    /// <summary>Ouvre un fichier de données (depuis le navigateur de contenu).</summary>
    public void OpenFile(string file)
    {
        Open(file);
        ImGui.SetWindowFocus("Données"u8);
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
