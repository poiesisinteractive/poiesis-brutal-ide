using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Orionum.Game;

/// <summary>
/// Ce qu'Orionum dessine par-dessus le monde : traînées des obus, missiles, rayons, explosions, HUD de bataille,
/// et les aides de débogage activables dans l'éditeur.
/// </summary>
public sealed class Visuals : IWorldVisualizer
{
    public const string Ranges = "Portées";
    public const string Targets = "Cibles";
    public const string Velocities = "Vitesses";
    public const string ShieldsChannel = "Boucliers";
    public const string Groups = "Escadres";

    public IReadOnlyList<string> DebugChannels { get; } = [Targets, Ranges, Velocities, ShieldsChannel, Groups];

    public static float4 FactionHull(Faction f) =>
        f == Faction.Coalition ? new float4(0.82f, 0.88f, 1.00f, 1) : new float4(0.78f, 0.50f, 0.30f, 1);

    public static float4 FactionFire(Faction f, float alpha = 1) =>
        f == Faction.Coalition ? new float4(0.45f, 0.80f, 1.00f, alpha) : new float4(1.00f, 0.55f, 0.15f, alpha);

    static readonly float4 Missile = new(1f, 0.95f, 0.6f, 1);
    static readonly float4 Explosion = new(1f, 0.75f, 0.35f, 1);

    public void Draw(World world, DrawContext ctx)
    {
        if (!world.TryResource<Battle>(out var battle) || !world.TryResource<Catalog>(out var catalog))
            return;

        // Traînées : longueur à l'échelle du zoom pour rester visibles de loin.
        for (int i = 0; i < battle.ProjectileCount; i++)
        {
            ref var p = ref battle.Projectiles[i];
            if (!p.Alive)
                continue;
            float speed = p.Velocity.Length();
            if (speed < 1)
                continue;
            float len = MathF.Max(speed * 0.06f, ctx.MetersForPixels(p.Position, p.Kind == WeaponKind.Missile ? 6 : 10));
            var color = p.Kind == WeaponKind.Missile ? Missile : FactionFire(p.Faction);
            ctx.Line(p.Position - p.Velocity / speed * len, p.Position, color);
        }

        foreach (var b in battle.Beams)
        {
            float a = Math.Clamp(b.Life / b.MaxLife, 0.2f, 1);
            ctx.Line(b.From, b.To, FactionFire(b.Faction, a));
        }

        foreach (var x in battle.Explosions)
        {
            float t = 1 - x.Life / x.MaxLife;
            float r = MathF.Max(x.Radius * (0.3f + t), ctx.MetersForPixels(x.Position, 4 + 10 * t));
            var c = Explosion with { W = 1 - t };
            SphereWire(ctx, x.Position, r, c);
        }

        DrawDebug(world, battle, catalog, ctx);
        DrawHud(world, battle, ctx);
    }

    static void DrawDebug(World world, Battle battle, Catalog catalog, DrawContext ctx)
    {
        var ships = world.Store<Ship>();
        var transforms = world.Store<Transform>();
        var motions = world.Store<Motion>();
        var dense = ships.DenseEntities;
        var items = ships.Components;

        if (ctx.IsEnabled(Targets) || ctx.IsEnabled(Velocities))
            for (int i = 0; i < dense.Length; i++)
            {
                var pos = transforms.Get(dense[i]).Position;
                if (ctx.IsEnabled(Targets) && world.IsAlive(items[i].Target))
                    ctx.Line(pos, transforms.Get(items[i].Target.Index).Position, FactionFire(items[i].Faction, 0.35f));
                if (ctx.IsEnabled(Velocities))
                    ctx.Line(pos, pos + motions.Get(dense[i]).Velocity * 10, new float4(0.5f, 1, 0.5f, 0.8f));
            }

        if (ctx.IsEnabled(Groups))
            for (int g = 0; g < battle.Groups.Length; g++)
            {
                var grp = battle.Groups[g];
                if (grp.Alive == 0)
                    continue;
                ctx.Label(grp.Centroid, $"escadre {g} · {grp.Alive} coques", FactionFire(grp.Faction));
                if (grp.TargetGroup >= 0)
                    ctx.Line(grp.Centroid, battle.Groups[grp.TargetGroup].Centroid, FactionFire(grp.Faction, 0.6f));
            }

        // Aides centrées sur la coque sélectionnée dans l'éditeur.
        int sel = ctx.SelectedEntity;
        if (sel < 0 || !ships.Has(sel))
            return;
        ref var s = ref ships.Get(sel);
        var cls = catalog.Classes[s.Class];
        var at = transforms.Get(sel).Position;
        var fwd = motions.Get(sel).Forward;
        if (ctx.IsEnabled(Ranges))
            foreach (var w in cls.Weapons)
                if (w.Def.Kind != WeaponKind.PointDefence)
                    ctx.Circle(at, w.Def.Range, FactionFire(s.Faction, 0.5f), 96);
        if (ctx.IsEnabled(ShieldsChannel))
        {
            var sh = world.Store<Shields>().Get(sel);
            float max = MathF.Max(cls.Def.ShieldFace, 1);
            var f = Frame.FromForward(fwd);
            float r = cls.Def.Radius * 1.6f;
            void FaceLabel(float3 dir, string name, float value) =>
                ctx.Label(at + dir * r, $"{name} {value / max:P0}", value / max > 0.3f ? new float4(0.5f, 0.85f, 1, 1) : new float4(1, 0.4f, 0.4f, 1));
            FaceLabel(f.Fwd, "avant", sh.Front);
            FaceLabel(-f.Fwd, "arrière", sh.Back);
            FaceLabel(f.Port, "bâbord", sh.Port);
            FaceLabel(-f.Port, "tribord", sh.Starboard);
            FaceLabel(f.Up, "dorsal", sh.Dorsal);
            FaceLabel(-f.Up, "ventral", sh.Ventral);
        }
        // Tourelles vivantes (points) et détruites (croix) de la coque sélectionnée.
        var bank = world.Store<WeaponBank>().Get(sel);
        float mark = ctx.MetersForPixels(at, 3);
        for (int w = 0; w < cls.Weapons.Length; w++)
        {
            var hps = cls.Weapons[w].Hardpoints;
            for (int t = 0; t < hps.Length; t++)
            {
                var p = Frame.ToWorld(at, fwd, hps[t]);
                bool alive = (bank[w].Alive & (1UL << t)) != 0;
                var c = alive ? new float4(0.4f, 1, 0.5f, 1) : new float4(1, 0.3f, 0.3f, 1);
                ctx.Line(p - new float3(mark, 0, 0), p + new float3(mark, 0, 0), c);
                ctx.Line(p - new float3(0, mark, 0), p + new float3(0, mark, 0), c);
            }
        }
    }

    static void DrawHud(World world, Battle battle, DrawContext ctx)
    {
        var ships = world.Store<Ship>();
        int coalition = 0, clans = 0;
        foreach (var s in ships.Components)
            if (s.Faction == Faction.Coalition)
                coalition++;
            else
                clans++;
        ctx.HudLine($"Coalition {coalition} coques (−{battle.Losses[0]})   Clans {clans} coques (−{battle.Losses[1]})", new float4(0.9f, 0.9f, 0.92f, 1));
        ctx.HudLine($"{battle.ProjectileCount} projectiles · {battle.ShotsFired} tirs · {battle.TurretsDestroyed} tourelles détruites · {battle.MissilesIntercepted} missiles abattus",
            new float4(0.6f, 0.6f, 0.65f, 1));
        if (battle.Winner is { } w)
            ctx.HudLine($"Victoire : {(w == Faction.Coalition ? "Coalition" : "Clans")} en {battle.EndTime / 60:0.0} min", FactionFire(w));
    }

    static void SphereWire(DrawContext ctx, float3 c, float r, float4 color)
    {
        const int n = 20;
        for (int i = 0; i < n; i++)
        {
            float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
            ctx.Line(c + new float3(MathF.Cos(a0), MathF.Sin(a0), 0) * r, c + new float3(MathF.Cos(a1), MathF.Sin(a1), 0) * r, color);
            ctx.Line(c + new float3(MathF.Cos(a0), 0, MathF.Sin(a0)) * r, c + new float3(MathF.Cos(a1), 0, MathF.Sin(a1)) * r, color);
            ctx.Line(c + new float3(0, MathF.Cos(a0), MathF.Sin(a0)) * r, c + new float3(0, MathF.Cos(a1), MathF.Sin(a1)) * r, color);
        }
    }
}
