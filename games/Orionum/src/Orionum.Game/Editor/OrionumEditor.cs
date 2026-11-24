using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Editor.Api;
using Poiesis.Engine.Scene;

namespace Orionum.Game.Editor;

/// <summary>
/// Les outils d'éditeur d'Orionum, rechargés à chaud avec le jeu :
/// - panneau « Ordre de bataille » : forces de chaque camp (coques, points de structure, puissance de feu) ;
/// - inspecteur des flottes : leur composition, classe par classe ;
/// - gizmos : rayon des astres (poignée), portée de sortie de croisière autour des flottes ;
/// - commande « Orionum/Aligner les flottes face à face ».
/// </summary>
public sealed class OrionumEditor : IEditorExtension
{
    static readonly float4 Coalition = new(0.45f, 0.7f, 1f, 1f);
    static readonly float4 Clans = new(1f, 0.55f, 0.3f, 1f);
    static readonly float4 Muted = new(0.6f, 0.6f, 0.65f, 1f);
    static readonly float4 Warning = new(1f, 0.75f, 0.3f, 1f);

    public void Register(EditorRegistry editor)
    {
        editor.Panel("Ordre de bataille", OrderOfBattle);
        editor.Inspector<FleetNode>("Composition", FleetComposition);
        editor.Gizmo<BodyNode>(BodyRadius);
        editor.Gizmo<FleetNode>(CruiseCutoff);
        editor.MenuItem("Orionum/Aligner les flottes face à face", FaceEachOther);
    }

    /// <summary>Les forces d'une flotte : coques, points de structure et dégâts par seconde nominaux (hors défense ponctuelle).</summary>
    public readonly record struct Strength(int Hulls, float HullPoints, float Firepower)
    {
        public static Strength operator +(Strength a, Strength b) => new(a.Hulls + b.Hulls, a.HullPoints + b.HullPoints, a.Firepower + b.Firepower);
    }

    public static Strength Of(Catalog catalog, FleetNode fleet)
    {
        var total = default(Strength);
        foreach (var entry in catalog.Fleet(fleet.Composition).Ships)
            total += Of(catalog.Classes[catalog.IndexOf(entry.Class)], entry.Count * Math.Max(1, fleet.Scale));
        return total;
    }

    static Strength Of(CompiledClass cls, int count)
    {
        float dps = 0;
        foreach (var w in cls.Weapons)
            if (w.Def.Kind != WeaponKind.PointDefence && w.Def.Rof > 0)
                dps += w.Hardpoints.Length * w.Def.Damage * w.Def.Salvo * w.Def.Rof * w.Def.HitChance;
        return new Strength(count, cls.Def.Hull * count, dps * count);
    }

    static Catalog? CatalogOf(IEditorContext ctx) =>
        ctx.Preview is { } world && world.TryResource<Catalog>(out var catalog) ? catalog : null;

    static float4 ColorOf(Faction f) => f == Faction.Coalition ? Coalition : Clans;

    static string Big(float v) => v >= 1e6f ? $"{v / 1e6f:0.0} M" : v >= 1e3f ? $"{v / 1e3f:0.0} k" : $"{v:0}";

    void OrderOfBattle(IEditorContext ctx)
    {
        if (CatalogOf(ctx) is not { } catalog)
        {
            ImGui.TextDisabled("Pas d'aperçu : la scène ne se cuit pas (voir le viewport)."u8);
            return;
        }
        var fleets = ctx.Scene.DescendantsAndSelf().OfType<FleetNode>().ToList();
        if (fleets.Count == 0)
        {
            ImGui.TextDisabled("Aucune flotte dans la scène."u8);
            return;
        }

        var totals = new Dictionary<Faction, Strength>();
        if (ImGui.BeginTable("oob"u8, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Flotte"u8);
            ImGui.TableSetupColumn("Échelle"u8);
            ImGui.TableSetupColumn("Coques"u8);
            ImGui.TableSetupColumn("Structure"u8);
            ImGui.TableSetupColumn("Feu (dégâts/s)"u8);
            ImGui.TableHeadersRow();
            foreach (var fleet in fleets)
            {
                Strength s;
                try
                {
                    s = Of(catalog, fleet);
                }
                catch (KeyNotFoundException)
                {
                    s = default;
                }
                totals[fleet.Faction] = totals.GetValueOrDefault(fleet.Faction) + s;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, ColorOf(fleet.Faction));
                if (ImGui.Selectable($"{fleet.Name}##{fleet.Id}", ctx.Selection == fleet, ImGuiSelectableFlags.SpanAllColumns))
                    ctx.Selection = fleet;
                ImGui.PopStyleColor();
                ImGui.TableNextColumn();
                ImGui.Text($"×{fleet.Scale}");
                ImGui.TableNextColumn();
                ImGui.Text($"{s.Hulls}");
                ImGui.TableNextColumn();
                ImGui.Text(Big(s.HullPoints));
                ImGui.TableNextColumn();
                ImGui.Text(Big(s.Firepower));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.SeparatorText("Rapport de forces"u8);
        foreach (var (faction, s) in totals.OrderBy(kv => kv.Key))
            ImGui.TextColored(ColorOf(faction), $"{faction} : {s.Hulls} coques · structure {Big(s.HullPoints)} · feu {Big(s.Firepower)}/s");
        if (totals.Count == 2)
        {
            var a = totals[Faction.Coalition];
            var b = totals[Faction.Clans];
            float fire = b.Firepower > 0 ? a.Firepower / b.Firepower : 0;
            float hull = b.HullPoints > 0 ? a.HullPoints / b.HullPoints : 0;
            // Une mesure simple : le temps qu'il faut à chaque camp pour détruire la structure de l'autre.
            float tA = a.Firepower > 0 ? b.HullPoints / a.Firepower : float.PositiveInfinity;
            float tB = b.Firepower > 0 ? a.HullPoints / b.Firepower : float.PositiveInfinity;
            ImGui.Text($"Coalition / Clans : feu ×{fire:0.00}, structure ×{hull:0.00}");
            ImGui.TextColored(MathF.Abs(MathF.Log(tB / tA)) > MathF.Log(1.25f) ? Warning : Muted,
                $"Pour tout détruire, sans boucliers : Coalition {tA:0} s, Clans {tB:0} s");
            ImGui.TextDisabled("Estimation nominale (tir × cadence × chance de toucher) ; la bataille réelle se mesure au panneau Analyse."u8);
        }
    }

    void FleetComposition(FleetNode fleet, IEditorContext ctx)
    {
        if (CatalogOf(ctx) is not { } catalog)
            return;
        FleetComposition composition;
        try
        {
            composition = catalog.Fleet(fleet.Composition);
        }
        catch (KeyNotFoundException)
        {
            ImGui.TextColored(Warning, $"Composition « {fleet.Composition} » inconnue.");
            return;
        }
        if (ImGui.BeginTable("composition"u8, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Classe"u8);
            ImGui.TableSetupColumn("Nombre"u8);
            ImGui.TableSetupColumn("Structure"u8);
            ImGui.TableSetupColumn("Feu/s"u8);
            ImGui.TableHeadersRow();
            foreach (var entry in composition.Ships)
            {
                var cls = catalog.Classes[catalog.IndexOf(entry.Class)];
                var s = Of(cls, entry.Count * Math.Max(1, fleet.Scale));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(cls.Def.DisplayName.Length > 0 ? cls.Def.DisplayName : cls.Def.Id);
                ImGui.TableNextColumn();
                ImGui.Text($"{s.Hulls}");
                ImGui.TableNextColumn();
                ImGui.Text(Big(s.HullPoints));
                ImGui.TableNextColumn();
                ImGui.Text(Big(s.Firepower));
            }
            ImGui.EndTable();
        }
        var total = Of(catalog, fleet);
        ImGui.TextColored(ColorOf(fleet.Faction), $"Total : {total.Hulls} coques, structure {Big(total.HullPoints)}, feu {Big(total.Firepower)}/s");
    }

    /// <summary>Le rayon d'un astre : un cercle et une poignée à faire glisser.</summary>
    static void BodyRadius(BodyNode body, IGizmoContext ctx)
    {
        var color = new float4(0.95f, 0.8f, 0.4f, 1f);
        ctx.Draw.Circle(body.Position, body.Radius, color);
        var handle = body.Position + new float3(body.Radius, 0, 0);
        ctx.Draw.Label(handle, $"rayon {body.Radius / 1000:0.#} km", color);
        if (ctx.Handle("radius", handle, color, out var dragged))
            ctx.Set(body, nameof(BodyNode.Radius), MathF.Max(100, float3.Distance(dragged, body.Position)));
    }

    /// <summary>Autour d'une flotte : la distance à l'ennemi sous laquelle elle quitte la croisière (réglée par le scénario).</summary>
    static void CruiseCutoff(FleetNode fleet, IGizmoContext ctx)
    {
        float km = 35;
        for (var n = fleet.Parent; n is not null; n = n.Parent)
            if (n is ScenarioNode scenario)
            {
                km = scenario.CruiseCutoffKm;
                break;
            }
        var color = ColorOf(fleet.Faction) * new float4(1, 1, 1, 0.6f);
        ctx.Draw.Circle(fleet.Position, km * 1000, color, 96);
        ctx.Draw.Label(fleet.Position + new float3(0, 0, km * 1000), $"fin de croisière ({km:0} km)", color);
    }

    /// <summary>Tourne chaque flotte vers le centre des flottes adverses (une seule commande annulable).</summary>
    static void FaceEachOther(IEditorContext ctx)
    {
        var fleets = ctx.Scene.DescendantsAndSelf().OfType<FleetNode>().ToList();
        int turned = 0;
        foreach (var fleet in fleets)
        {
            var enemies = fleets.Where(f => f.Faction != fleet.Faction).ToList();
            if (enemies.Count == 0)
                continue;
            var center = enemies.Aggregate(float3.Zero, (acc, f) => acc + f.Position) / enemies.Count;
            var to = center - fleet.Position;
            if (to.X * to.X + to.Z * to.Z < 1)
                continue;
            float heading = MathF.Round(MathF.Atan2(to.X, to.Z) * 180 / MathF.PI, 1);
            if (MathF.Abs(heading - fleet.Heading) > 0.05f)
            {
                ctx.Set(fleet, nameof(FleetNode.Heading), heading);
                turned++;
            }
        }
        ctx.Log(turned == 0 ? "Les flottes se font déjà face." : $"{turned} flotte(s) tournée(s) vers l'ennemi.");
    }
}
