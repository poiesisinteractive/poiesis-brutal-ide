using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>
/// Le HUD de bataille d'Orionum : bandeau des forces (coques restantes, intégrité des survivants), horloge, fiche du
/// vaisseau sélectionné (coque, six faces de bouclier, tourelles, sous-systèmes) et bannière de victoire.
/// </summary>
public static class Hud
{
    static readonly float4 Panel = new(0.04f, 0.045f, 0.07f, 0.78f);
    static readonly float4 Frame = new(1, 1, 1, 0.12f);
    static readonly float4 TextColor = new(0.88f, 0.9f, 0.95f, 1);
    static readonly float4 Muted = new(0.6f, 0.62f, 0.7f, 1);
    static readonly float4 Bad = new(1f, 0.35f, 0.3f, 1);

    static uint Col(float4 c) => ImGui.GetColorU32(c);

    public readonly record struct Side(int Alive, int Lost, float HullLeft, float HullMax);

    /// <summary>Les forces d'un camp : coques en vie et perdues, structure restante des survivants.</summary>
    public static Side SideOf(World world, Battle battle, Catalog catalog, Faction faction)
    {
        int alive = 0;
        float left = 0, max = 0;
        var ships = world.Store<Ship>();
        foreach (ref readonly var s in ships.Components)
            if (s.Faction == faction && s.Hull > 0)
            {
                alive++;
                left += s.Hull;
                max += catalog.Classes[s.Class].Def.Hull;
            }
        return new Side(alive, battle.Losses[(int)faction], left, max);
    }

    public static void Draw(World world, HudContext ctx)
    {
        if (!world.TryResource<Battle>(out var battle) || !world.TryResource<Catalog>(out var catalog))
            return;
        var dl = ImGui.GetWindowDrawList();
        Banner(world, battle, catalog, ctx, dl);
        if (ctx.SelectedEntity >= 0 && !world.EntityAt(ctx.SelectedEntity).IsNull && world.Store<Ship>().Has(ctx.SelectedEntity))
            ShipCard(world, catalog, ctx, dl, ctx.SelectedEntity);
        if (battle.Winner is { } winner && !ctx.InEditor)
            Victory(ctx, dl, winner, battle.EndTime);
    }

    static void Banner(World world, Battle battle, Catalog catalog, HudContext ctx, ImDrawListPtr dl)
    {
        float w = MathF.Min(640, ctx.Size.X - 40), h = 62;
        var min = ctx.Min + new float2((ctx.Size.X - w) / 2, ctx.InEditor ? 34 : 12);
        dl.AddRectFilled(min, min + new float2(w, h), Col(Panel), 6);
        dl.AddRect(min, min + new float2(w, h), Col(Frame), 6);

        var time = TimeSpan.FromSeconds(ctx.Time);
        string clock = $"{(int)time.TotalMinutes:00}:{time.Seconds:00}{(ctx.TimeScale == 0 ? "  PAUSE" : ctx.TimeScale != 1 ? $"  ×{ctx.TimeScale:0.##}" : "")}";
        var clockSize = ImGui.CalcTextSize(clock);
        dl.AddText(min + new float2((w - clockSize.X) / 2, 6), Col(TextColor), clock);

        float half = (w - 30) / 2;
        Gauge(dl, min + new float2(10, 26), half, SideOf(world, battle, catalog, Faction.Coalition), "COALITION", Visuals.FactionFire(Faction.Coalition), rightToLeft: false);
        Gauge(dl, min + new float2(20 + half, 26), half, SideOf(world, battle, catalog, Faction.Clans), "CLANS", Visuals.FactionFire(Faction.Clans), rightToLeft: true);
    }

    static void Gauge(ImDrawListPtr dl, float2 at, float width, Side side, string name, float4 color, bool rightToLeft)
    {
        int total = Math.Max(side.Alive + side.Lost, 1);
        float share = side.Alive / (float)total;
        float integrity = side.HullMax > 0 ? side.HullLeft / side.HullMax : 0;
        string label = $"{name}  {side.Alive}/{total}";
        string detail = $"{integrity * 100:0} %";
        var barMin = at + new float2(0, 18);
        var barMax = barMin + new float2(width, 10);
        dl.AddRectFilled(barMin, barMax, Col(new float4(1, 1, 1, 0.08f)), 3);
        float fill = width * share;
        var fMin = rightToLeft ? new float2(barMax.X - fill, barMin.Y) : barMin;
        var fMax = rightToLeft ? barMax : new float2(barMin.X + fill, barMax.Y);
        dl.AddRectFilled(fMin, fMax, Col(color with { W = 0.9f }), 3);
        // L'intégrité des survivants, en trait fin sous la jauge.
        float sub = width * share * integrity;
        var sMin = rightToLeft ? new float2(barMax.X - sub, barMax.Y + 2) : new float2(barMin.X, barMax.Y + 2);
        dl.AddRectFilled(sMin, sMin + new float2(sub, 2), Col(TextColor with { W = 0.6f }));
        var labelSize = ImGui.CalcTextSize(label);
        var detailSize = ImGui.CalcTextSize(detail);
        if (rightToLeft)
        {
            dl.AddText(new float2(at.X + width - labelSize.X, at.Y), Col(color), label);
            dl.AddText(at, Col(Muted), detail);
        }
        else
        {
            dl.AddText(at, Col(color), label);
            dl.AddText(new float2(at.X + width - detailSize.X, at.Y), Col(Muted), detail);
        }
    }

    static void ShipCard(World world, Catalog catalog, HudContext ctx, ImDrawListPtr dl, int e)
    {
        ref readonly var ship = ref world.Store<Ship>().Get(e);
        var cls = catalog.Classes[ship.Class];
        var def = cls.Def;
        float w = 300, h = 172;
        var min = ctx.Min + new float2(14, ctx.Size.Y - h - (ctx.InEditor ? 14 : 44));
        dl.AddRectFilled(min, min + new float2(w, h), Col(Panel), 6);
        dl.AddRect(min, min + new float2(w, h), Col(Visuals.FactionFire(ship.Faction) with { W = 0.5f }), 6);
        var at = min + new float2(10, 8);
        dl.AddText(at, Col(Visuals.FactionFire(ship.Faction)), def.DisplayName.Length > 0 ? def.DisplayName : def.Id);
        dl.AddText(at + new float2(0, 18), Col(Muted), $"{ship.Faction} · {def.Role} · {def.LengthMeters:0} m");

        Bar(dl, at + new float2(0, 42), w - 20, "Coque", ship.Hull / MathF.Max(def.Hull, 1), new float4(0.85f, 0.85f, 0.9f, 1));
        if (world.Store<Shields>().Has(e))
        {
            var shields = world.Store<Shields>().Get(e);
            dl.AddText(at + new float2(0, 66), Col(Muted), "Boucliers"u8);
            string[] faces = ["Av", "Ar", "Bâ", "Tr", "Do", "Ve"];
            float cell = (w - 20 - 70) / 6;
            for (int f = 0; f < 6; f++)
            {
                var c = at + new float2(70 + f * cell, 66);
                float v = shields[(Face)f] / MathF.Max(def.ShieldFace, 1);
                dl.AddRectFilled(c + new float2(0, 4), c + new float2(cell - 4, 14), Col(new float4(1, 1, 1, 0.08f)), 2);
                dl.AddRectFilled(c + new float2(0, 4), c + new float2((cell - 4) * Math.Clamp(v, 0, 1), 14), Col(new float4(0.35f, 0.8f, 1f, 0.9f)), 2);
                dl.AddText(c + new float2(0, 16), Col(Muted), faces[f]);
            }
        }
        if (world.Store<WeaponBank>().Has(e))
        {
            ref var bank = ref world.Store<WeaponBank>().Get(e);
            int alive = 0, total = 0;
            for (int i = 0; i < cls.Weapons.Length; i++)
            {
                total += cls.Weapons[i].Hardpoints.Length;
                alive += System.Numerics.BitOperations.PopCount(bank[i].Alive);
            }
            dl.AddText(at + new float2(0, 104), Col(TextColor), $"Tourelles : {alive} / {total}");
        }
        if (world.Store<Subsystems>().Has(e))
        {
            var subs = world.Store<Subsystems>().Get(e);
            string State(bool off, string name) => off ? $"{name} HS" : name;
            var line = $"{State(subs.EnginesOffline, "Moteurs")} · {State(subs.WeaponsOffline, "Armes")} · {State(subs.ShieldsOffline, "Boucliers")}";
            bool any = subs.EnginesOffline || subs.WeaponsOffline || subs.ShieldsOffline;
            dl.AddText(at + new float2(0, 124), Col(any ? Bad : Muted), line);
        }
        dl.AddText(at + new float2(0, 144), Col(Muted with { W = 0.7f }), "Clic dans le vide : fermer la fiche"u8);
    }

    static void Bar(ImDrawListPtr dl, float2 at, float width, string label, float value, float4 color)
    {
        dl.AddText(at, Col(Muted), label);
        var min = at + new float2(70, 4);
        var max = at + new float2(width - 46, 14);
        dl.AddRectFilled(min, max, Col(new float4(1, 1, 1, 0.08f)), 2);
        float v = Math.Clamp(value, 0, 1);
        dl.AddRectFilled(min, new float2(min.X + (max.X - min.X) * v, max.Y), Col(v < 0.3f ? Bad : color), 2);
        dl.AddText(new float2(max.X + 8, at.Y), Col(TextColor), $"{v * 100:0} %");
    }

    static void Victory(HudContext ctx, ImDrawListPtr dl, Faction winner, double endTime)
    {
        string title = winner == Faction.Coalition ? "VICTOIRE DE LA COALITION" : "VICTOIRE DES CLANS";
        ImGui.PushFont(ImGui.GetFont(), 34);
        var size = ImGui.CalcTextSize(title);
        var at = ctx.Min + new float2((ctx.Size.X - size.X) / 2, ctx.Size.Y * 0.22f);
        dl.AddRectFilled(at - new float2(24, 12), at + size + new float2(24, 12), Col(Panel), 8);
        dl.AddText(at, Col(Visuals.FactionFire(winner)), title);
        ImGui.PopFont();
        var sub = $"en {TimeSpan.FromSeconds(endTime):mm\\:ss}";
        var subSize = ImGui.CalcTextSize(sub);
        dl.AddText(ctx.Min + new float2((ctx.Size.X - subSize.X) / 2, at.Y - ctx.Min.Y + size.Y + 16), Col(Muted), sub);
    }
}
