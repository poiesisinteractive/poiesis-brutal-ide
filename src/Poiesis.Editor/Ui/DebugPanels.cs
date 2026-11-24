using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Ui;

/// <summary>
/// Les entités du monde affiché (partie ou aperçu) et leurs composants. En jeu, les champs sont éditables en direct :
/// nombres, booléens, enums, vecteurs et structures imbriquées (boucliers par face, batteries…).
/// </summary>
sealed class EntitiesPanel(EditorSession session)
{
    string _filter = "";

    public void Draw()
    {
        if (ImGui.Begin("Entités"u8))
        {
            var world = session.Play?.World ?? session.Preview;
            var bake = session.Play?.Bake ?? session.PreviewBake;
            if (world is null)
            {
                ImGui.TextDisabled("Aucun monde."u8);
                ImGui.End();
                return;
            }
            ImGui.Text($"{world.EntityCount} entités · pas {world.Tick}");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            Widgets.TextField("##filtre", "entities-filter", ref _filter, 64);
            ImGui.SameLine();
            ImGui.TextDisabled("filtre"u8);

            var stores = world.Stores.OrderBy(s => s.ComponentType.Name).ToArray();
            var listHeight = ImGui.GetContentRegionAvail().Y * 0.4f;
            if (ImGui.BeginChild("list"u8, new float2(0, listHeight), ImGuiChildFlags.Borders))
            {
                var alive = new SortedSet<int>();
                foreach (var s in stores)
                    foreach (var e in s.DenseEntities)
                        alive.Add(e);
                var rows = alive.Select(e => (Index: e, Label: Label(world, bake, e))).Where(r => _filter.Length == 0 || r.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToArray();
                var clipper = new ImGuiListClipper();
                clipper.Begin(rows.Length);
                while (clipper.Step())
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        if (ImGui.Selectable(rows[i].Label, rows[i].Index == session.SelectedEntity))
                            session.SelectedEntity = rows[i].Index;
                clipper.End();
            }
            ImGui.EndChild();

            int sel = session.SelectedEntity;
            if (sel >= 0 && world.EntityAt(sel).IsNull)
            {
                ImGui.TextColored(EditorTheme.Warning, $"#{sel} n'existe plus (détruite).");
            }
            else if (sel >= 0)
            {
                bool editable = session.Play is not null;
                if (!editable)
                    ImGui.TextDisabled("Aperçu : lancez la partie pour éditer en direct."u8);
                else if (session.PlayState == PlayState.Playing)
                    ImGui.TextDisabled("Astuce : mettez en pause pour éditer sans que la sim écrase vos valeurs."u8);
                foreach (var s in stores.Where(s => s.Has(sel)))
                {
                    if (!ImGui.CollapsingHeader(s.ComponentType.Name, ImGuiTreeNodeFlags.DefaultOpen))
                        continue;
                    ImGui.PushID(s.ComponentType.Name);
                    ImGui.BeginDisabled(!editable);
                    var boxed = s.GetBoxed(sel);
                    if (DrawStruct(boxed, s.ComponentType))
                        s.SetBoxed(sel, boxed);
                    ImGui.EndDisabled();
                    ImGui.PopID();
                }
            }
        }
        ImGui.End();
    }

    /// <summary>« #12 Coalition · Ship, Motion… » : le nœud propriétaire et les composants.</summary>
    string Label(World world, BakeContext? bake, int e)
    {
        var entity = world.EntityAt(e);
        string owner = bake is not null && bake.OwnerOf.TryGetValue(entity, out var id) && session.FindNode(id) is { } n ? n.Name : "";
        var comps = string.Join(", ", world.Stores.Where(s => s.Has(e)).Select(s => s.ComponentType.Name).Order());
        return owner.Length > 0 ? $"#{e}  {owner} · {comps}" : $"#{e}  {comps}";
    }

    /// <summary>Édite les champs publics d'une structure boxée ; rend vrai si une valeur a changé.</summary>
    static bool DrawStruct(object boxed, Type type, int depth = 0)
    {
        bool changed = false;
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            ImGui.PushID(f.Name);
            var value = f.GetValue(boxed);
            if (TryEdit(f.Name, f.FieldType, ref value))
            {
                f.SetValue(boxed, value);
                changed = true;
            }
            else if (value is not null && IsPlainStruct(f.FieldType) && depth < 3)
            {
                if (ImGui.TreeNodeEx(f.Name, ImGuiTreeNodeFlags.SpanAvailWidth))
                {
                    if (DrawStruct(value, f.FieldType, depth + 1))
                    {
                        f.SetValue(boxed, value);   // la copie boxée modifiée remplace le champ
                        changed = true;
                    }
                    ImGui.TreePop();
                }
            }
            ImGui.PopID();
        }
        return changed;
    }

    static bool IsPlainStruct(Type t) => t.IsValueType && !t.IsPrimitive && !t.IsEnum && t.Namespace?.StartsWith("Brutal") != true;

    static bool TryEdit(string label, Type type, ref object? value)
    {
        switch (value)
        {
            case float f:
                if (ImGui.DragFloat(label, ref f, MathF.Max(MathF.Abs(f) * 0.01f, 0.01f), 0, 0, "%.2f"u8)) { value = f; return true; }
                return false;
            case double d:
                var df = (float)d;
                if (ImGui.DragFloat(label, ref df, MathF.Max(MathF.Abs(df) * 0.01f, 0.01f), 0, 0, "%.3f"u8)) { value = (double)df; return true; }
                return false;
            case int i:
                if (ImGui.DragInt(label, ref i)) { value = i; return true; }
                return false;
            case bool b:
                if (ImGui.Checkbox(label, ref b)) { value = b; return true; }
                return false;
            case ulong u:
                ImGui.Text($"{label} = {Convert.ToString((long)u, 2).PadLeft(8, '0')} ({System.Numerics.BitOperations.PopCount(u)})");
                return false;
            case float3 v:
                if (ImGui.DragFloat3(label, ref v, MathF.Max(v.Length() * 0.002f, 0.1f), 0, 0, "%.1f"u8)) { value = v; return true; }
                return false;
            case Entity en:
                ImGui.Text(en.IsNull ? $"{label} = (aucune)" : $"{label} = #{en.Index}");
                return false;
            case Enum en:
                bool picked = false;
                if (ImGui.BeginCombo(label, en.ToString()))
                {
                    foreach (var option in Enum.GetValues(type))
                        if (ImGui.Selectable(option.ToString() ?? "", option.Equals(value)))
                        {
                            value = option;
                            picked = true;
                        }
                    ImGui.EndCombo();
                }
                return picked;
            case floatQuat q:
                ImGui.Text($"{label} = ({q.X:0.##}, {q.Y:0.##}, {q.Z:0.##}, {q.W:0.##})");
                return false;
            case float4 c:
                if (ImGui.ColorEdit4(label, ref c)) { value = c; return true; }
                return false;
            default:
                if (value is null || !IsPlainStruct(type))
                {
                    ImGui.Text($"{label} = {value}");
                    return false;
                }
                return false;   // structure imbriquée : dépliée par l'appelant
        }
    }
}

/// <summary>
/// Le débogage de la partie : pas à pas, instantanés (prendre, revenir), dessins de débogage du jeu par canal,
/// et profileur (temps de chaque système, à chaque pas).
/// </summary>
sealed class DebugPanel(EditorSession session, LogBuffer log)
{
    public HashSet<string> EnabledChannels { get; } = new(StringComparer.Ordinal);

    public void Draw()
    {
        if (ImGui.Begin("Débogage"u8))
        {
            var play = session.Play;
            if (play is null)
            {
                ImGui.TextDisabled("Lancez la partie (F5) pour déboguer."u8);
                DrawChannels();
                ImGui.End();
                return;
            }

            var io = ImGui.GetIO();
            ImGui.Text($"t = {play.Time:0.00} s · pas {play.World.Tick} · {io.Framerate:0} images/s");
            ImGui.SameLine();
            bool paused = session.PlayState == PlayState.Paused;
            if (ImGui.SmallButton(paused ? "▶ Reprendre" : "❚❚ Pause"))
                session.TogglePause();
            ImGui.SameLine();
            ImGui.BeginDisabled(!paused);
            if (ImGui.SmallButton("Pas +1"u8))
                session.StepOnce();
            ImGui.SameLine();
            if (ImGui.SmallButton("+1 s"u8))
                play.Run((int)Math.Round(1 / play.Clock.StepSeconds));
            ImGui.EndDisabled();

            if (ImGui.CollapsingHeader("Instantanés"u8, ImGuiTreeNodeFlags.DefaultOpen))
                DrawSnapshots(play);
            if (ImGui.CollapsingHeader("Dessins de débogage"u8, ImGuiTreeNodeFlags.DefaultOpen))
                DrawChannels();
            if (ImGui.CollapsingHeader("Profileur"u8, ImGuiTreeNodeFlags.DefaultOpen))
                DrawProfiler(play);
        }
        ImGui.End();
    }

    void DrawSnapshots(PlaySession play)
    {
        if (ImGui.Button("Prendre un instantané (F9)"u8))
            Take(play);
        Snapshot? restore = null, delete = null;
        foreach (var snap in play.Snapshots)
        {
            ImGui.PushID(snap.GetHashCode());
            ImGui.Text($"{snap.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Revenir"u8))
                restore = snap;
            ImGui.SameLine();
            if (ImGui.SmallButton("×"u8))
                delete = snap;
            ImGui.PopID();
        }
        if (restore is not null)
        {
            play.Restore(restore);
            log.Info("débogage", $"Retour à l'instantané « {restore.Name} ».");
        }
        if (delete is not null)
            play.DeleteSnapshot(delete);
    }

    public void Take(PlaySession play)
    {
        var snap = play.TakeSnapshot();
        log.Info("débogage", $"Instantané « {snap.Name} » pris.");
    }

    void DrawChannels()
    {
        if (session.Modules.Module is not IWorldVisualizer visualizer)
        {
            ImGui.TextDisabled("Le jeu ne propose pas de dessins de débogage."u8);
            return;
        }
        foreach (var channel in visualizer.DebugChannels)
        {
            bool on = EnabledChannels.Contains(channel);
            if (ImGui.Checkbox(channel, ref on))
            {
                if (on)
                    EnabledChannels.Add(channel);
                else
                    EnabledChannels.Remove(channel);
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    static void DrawProfiler(PlaySession play)
    {
        var total = play.Schedule.Total;
        ImGui.Text($"Pas : {total.LastMs:0.00} ms (moyenne {total.AverageMs:0.00}, pic {total.MaxMs:0.00}) · budget {play.Clock.StepSeconds * 1000:0} ms");
        ImGui.PlotLines("##total"u8, total.History(), 0, default, 0, float.MaxValue, new float2(-1, 50));
        if (!ImGui.BeginTable("systems"u8, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Système"u8);
        ImGui.TableSetupColumn("Moyenne"u8);
        ImGui.TableSetupColumn("Pic"u8);
        ImGui.TableSetupColumn("Historique"u8);
        ImGui.TableHeadersRow();
        double sum = Math.Max(total.AverageMs, 1e-6);
        foreach (var t in play.Schedule.Timings)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(t.Name);
            ImGui.TableNextColumn();
            ImGui.Text($"{t.AverageMs:0.000} ms ({t.AverageMs / sum:P0})");
            ImGui.TableNextColumn();
            ImGui.Text($"{t.MaxMs:0.00}");
            ImGui.TableNextColumn();
            ImGui.PlotLines($"##{t.Name}", t.History(), 0, default, 0, float.MaxValue, new float2(-1, 18));
        }
        ImGui.EndTable();
    }
}
