using System.Reflection;
using System.Text.Json.Nodes;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>Petits widgets communs aux panneaux.</summary>
static class Widgets
{
    static readonly Dictionary<string, ImInputString> TextBuffers = new();

    public static ImColor8 Col(float4 c) => ImGui.GetColorU32(c);

    /// <summary>Champ texte lié à une chaîne. Rend vrai quand la saisie est validée (Entrée ou perte du focus).</summary>
    public static bool TextField(string label, string id, ref string value, int capacity = 256, ImGuiInputTextFlags flags = default)
    {
        if (!TextBuffers.TryGetValue(id, out var buffer))
            TextBuffers[id] = buffer = new ImInputString(capacity, value);
        else if (!IsActive(id) && buffer.ToString() != value)
            buffer.SetValue(value.AsSpan());   // la valeur a changé ailleurs (annuler, autre panneau)
        ImGui.PushID(id);
        ImGui.InputText(label, buffer, flags);
        bool done = ImGui.IsItemDeactivatedAfterEdit() || (flags.HasFlag(ImGuiInputTextFlags.EnterReturnsTrue) && ImGui.IsItemDeactivated());
        _active[id] = ImGui.IsItemActive();
        ImGui.PopID();
        if (done)
        {
            value = buffer.ToString();
            return true;
        }
        return false;
    }

    /// <summary>Zone de texte multiligne, dont le contenu est lu à la demande (description de feature).</summary>
    public static ImInputString Multiline(string id, int capacity = 8192)
    {
        if (!TextBuffers.TryGetValue(id, out var buffer))
            TextBuffers[id] = buffer = new ImInputString(capacity, ""u8);
        return buffer;
    }

    static readonly Dictionary<string, bool> _active = new();

    static bool IsActive(string id) => _active.TryGetValue(id, out var a) && a;

    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)"u8);
        if (ImGui.BeginItemTooltip())
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.Text(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// <summary>Le « nom lisible » d'une propriété : <c>DurationSeconds</c> devient « Duration seconds ».</summary>
    public static string Pretty(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Inspecteur générique : un widget par propriété, choisi d'après son type et ses attributs
/// (<see cref="RangeAttribute"/>, <see cref="ContentRefAttribute"/>). Chaque modification validée passe par
/// une commande annulable ; pendant un glissement, la valeur est appliquée en direct puis enregistrée au relâchement.
/// </summary>
sealed class PropertyEditor(EditorSession session, ProjectConfig project)
{
    object? _dragStartValue;
    PropertyInfo? _dragProperty;
    Node? _dragNode;

    public void Draw(Node node)
    {
        string name = node.Name;
        if (Widgets.TextField("Nom", $"name-{node.Id}", ref name) && name != node.Name)
            session.History.Execute(new RenameCommand(node, name));

        ImGui.Separator();
        foreach (var p in SceneSerializer.EditableProperties(node.GetType()))
        {
            ImGui.PushID(p.Name);
            DrawProperty(node, p);
            if (p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is { } desc)
                Widgets.HelpMarker(desc.Description);
            ImGui.PopID();
        }
    }

    void DrawProperty(Node node, PropertyInfo p)
    {
        var label = Widgets.Pretty(p.Name);
        var type = p.PropertyType;
        var value = p.GetValue(node);
        var range = p.GetCustomAttribute<RangeAttribute>();

        if (type == typeof(float))
        {
            var v = (float)value!;
            bool changed = ImGui.DragFloat(label, ref v, range?.Speed ?? 1f, range?.Min ?? 0, range?.Max ?? 0, "%.2f"u8);
            Live(node, p, v, changed);
        }
        else if (type == typeof(int))
        {
            var v = (int)value!;
            bool changed = ImGui.DragInt(label, ref v, range?.Speed ?? 0.2f, (int)(range?.Min ?? 0), (int)(range?.Max ?? 0));
            Live(node, p, v, changed);
        }
        else if (type == typeof(float3))
        {
            var v = (float3)value!;
            bool changed = ImGui.DragFloat3(label, ref v, range?.Speed ?? 10f, 0, 0, "%.0f"u8);
            Live(node, p, v, changed);
        }
        else if (type == typeof(bool))
        {
            var v = (bool)value!;
            if (ImGui.Checkbox(label, ref v))
                session.History.Execute(new SetPropertyCommand(node, p, v));
        }
        else if (type.IsEnum)
        {
            if (ImGui.BeginCombo(label, value!.ToString() ?? ""))
            {
                foreach (var option in Enum.GetValues(type))
                    if (ImGui.Selectable(option.ToString()!, option.Equals(value)) && !option.Equals(value))
                        session.History.Execute(new SetPropertyCommand(node, p, option));
                ImGui.EndCombo();
            }
        }
        else if (type == typeof(string) && p.GetCustomAttribute<ContentRefAttribute>() is { } contentRef)
        {
            var current = (string?)value ?? "";
            var ids = ContentIds(contentRef);
            bool missing = current.Length > 0 && !ids.Contains(current);
            if (missing)
                ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            if (ImGui.BeginCombo(label, current.Length == 0 ? "(aucun)" : current))
            {
                foreach (var id in ids)
                    if (ImGui.Selectable(id, id == current) && id != current)
                        session.History.Execute(new SetPropertyCommand(node, p, id));
                ImGui.EndCombo();
            }
            if (missing)
            {
                ImGui.PopStyleColor();
                Widgets.HelpMarker($"« {current} » n'existe pas dans content/{contentRef.Folder}.");
            }
        }
        else if (type == typeof(string))
        {
            var v = (string?)value ?? "";
            if (Widgets.TextField(label, $"{node.Id}-{p.Name}", ref v) && v != (string?)value)
                session.History.Execute(new SetPropertyCommand(node, p, v));
        }
        else
        {
            ImGui.TextDisabled($"{label} : {value} ({type.Name}, non éditable)");
        }
    }

    /// <summary>Applique la valeur en direct pendant le glissement ; au relâchement, en fait une commande annulable.</summary>
    void Live(Node node, PropertyInfo p, object newValue, bool changed)
    {
        if (ImGui.IsItemActivated())
        {
            _dragNode = node;
            _dragProperty = p;
            _dragStartValue = p.GetValue(node);
        }
        if (changed)
        {
            p.SetValue(node, newValue);
            session.Touch();
        }
        if (ImGui.IsItemDeactivated() && _dragNode == node && _dragProperty == p)
        {
            var final = p.GetValue(node);
            p.SetValue(node, _dragStartValue);
            if (!Equals(final, _dragStartValue))
                session.History.Execute(new SetPropertyCommand(node, p, final));
            _dragNode = null;
            _dragProperty = null;
        }
    }

    readonly Dictionary<string, (DateTime Stamp, List<string> Ids)> _contentCache = new();

    /// <summary>Identifiants (<c>id</c>) des fichiers de contenu d'un dossier ; relus quand le dossier change.</summary>
    List<string> ContentIds(ContentRefAttribute r)
    {
        var dir = Path.Combine(project.ContentRoot, r.Folder);
        var stamp = Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir) : DateTime.MinValue;
        var key = r.Folder + "|" + r.Pattern;
        if (_contentCache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
            return cached.Ids;
        var ids = new List<string>();
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, r.Pattern).Order(StringComparer.Ordinal))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file))?["id"]?.GetValue<string>() is { } id)
                        ids.Add(id);
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        _contentCache[key] = (stamp, ids);
        return ids;
    }

    public void InvalidateContent() => _contentCache.Clear();
}
