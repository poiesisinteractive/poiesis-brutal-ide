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

        Effects.Draw(world, battle, catalog, ctx);

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

/// <summary>
/// Les effets de bataille, en couleurs HDR (au-delà de 1, elles débordent en halo par le bloom) : traînées et têtes
/// lumineuses des obus et missiles, rayons, gerbes hexagonales sur les boucliers, étincelles sur les coques, boules de
/// feu, éclairs et éclats des explosions. Tailles minimales à l'écran, pour que la bataille se lise de loin.
/// </summary>
static class Effects
{
    static float4 Hdr(float4 c, float k) => new(c.X * k, c.Y * k, c.Z * k, c.W);

    /// <summary>
    /// Rayon d'une lueur, au moins <paramref name="pixels"/> à l'écran, et facteur d'intensité : grossie pour rester
    /// visible de loin, elle s'atténue d'autant (sinon, vue de loin, la bataille n'est qu'une tache blanche).
    /// </summary>
    static (float Radius, float Gain) Visible(DrawContext ctx, float3 at, float meters, float pixels)
    {
        float min = ctx.MetersForPixels(at, pixels);
        return meters >= min ? (meters, 1) : (min, Math.Clamp(meters / min, 0.15f, 1));
    }

    public static float4 ShieldColor(Faction f) => f == Faction.Coalition ? new float4(0.35f, 0.8f, 1f, 1) : new float4(1f, 0.65f, 0.25f, 1);

    static readonly float4 Fire = new(1f, 0.55f, 0.18f, 1);
    static readonly float4 Flash = new(1f, 0.95f, 0.85f, 1);
    static readonly float4 MissileGlow = new(1f, 0.9f, 0.55f, 1);

    public static void Draw(World world, Battle battle, Catalog catalog, DrawContext ctx)
    {
        Projectiles(battle, ctx);
        Beams(battle, ctx);
        ShieldHits(world, battle, catalog, ctx);
        HullHits(battle, ctx);
        Explosions(battle, ctx);
    }

    static void Projectiles(Battle battle, DrawContext ctx)
    {
        for (int i = 0; i < battle.ProjectileCount; i++)
        {
            ref var p = ref battle.Projectiles[i];
            if (!p.Alive)
                continue;
            float speed = p.Velocity.Length();
            if (speed < 1)
                continue;
            bool missile = p.Kind == WeaponKind.Missile;
            float len = MathF.Max(speed * 0.06f, ctx.MetersForPixels(p.Position, missile ? 8 : 12));
            var color = missile ? MissileGlow : Visuals.FactionFire(p.Faction);
            var tail = p.Position - p.Velocity / speed * len;
            ctx.Line(tail, p.Position, Hdr(color, 2.2f));
            var (pr, pk) = Visible(ctx, p.Position, missile ? 25 : 10, missile ? 5 : 3);
            ctx.Glow(p.Position, pr, Hdr(color, (missile ? 1.6f : 1.1f) * MathF.Max(pk, 0.4f)));
        }
    }

    static void Beams(Battle battle, DrawContext ctx)
    {
        foreach (var b in battle.Beams)
        {
            float a = Math.Clamp(b.Life / b.MaxLife, 0.15f, 1);
            var color = Visuals.FactionFire(b.Faction);
            ctx.Line(b.From, b.To, Hdr(color, 4 * a));
            var (r, k) = Visible(ctx, b.To, b.Width * 12, 3 + b.Width * 2);
            ctx.Glow(b.To, r, Hdr(color, 1.5f * a * k));
            ctx.Glow(b.From, r * 0.5f, Hdr(color, 1f * a * k));
        }
    }

    /// <summary>
    /// Un coup sur un bouclier : sept hexagones (un centre, six autour) posés sur la bulle du vaisseau, du côté du tir,
    /// qui s'éteignent en ~0,6 s. Vus de trop loin pour qu'on distingue les cellules : une simple lueur.
    /// </summary>
    static void ShieldHits(World world, Battle battle, Catalog catalog, DrawContext ctx)
    {
        var transforms = world.Store<Transform>();
        var motions = world.Store<Motion>();
        var ships = world.Store<Ship>();
        foreach (var h in battle.ShieldHits)
        {
            if (!world.IsAlive(h.Ship) || !transforms.Has(h.Ship.Index) || !motions.Has(h.Ship.Index))
                continue;
            var cls = catalog.Classes[ships.Get(h.Ship.Index).Class].Def;
            var center = transforms.Get(h.Ship.Index).Position;
            var frame = Frame.FromForward(motions.Get(h.Ship.Index).Forward);
            var dir = float3.NormalizeOrZero(frame.Port * h.LocalDir.X + frame.Up * h.LocalDir.Y + frame.Fwd * h.LocalDir.Z);
            if (dir.LengthSquared() < 0.5f)
                continue;
            float fade = h.Life / h.MaxLife;
            float radius = cls.Radius * 1.15f;
            var color = ShieldColor(h.Faction);
            var at = center + dir * radius;
            float cell = radius * 0.11f * h.Strength;
            if (cell < ctx.MetersForPixels(at, 2.5f))
            {
                ctx.Glow(at, MathF.Max(cell * 3, ctx.MetersForPixels(at, 3)), Hdr(color, 1.6f * fade));
                continue;
            }
            var a = Frame.SafeNormalize(float3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0)), new float3(1, 0, 0));
            var b = float3.Cross(dir, a);
            // Le centre, puis l'anneau de six (pavage hexagonal : voisins à √3 × le rayon de cellule).
            Hexagon(ctx, center, radius, at, a, b, 0, 0, cell, Hdr(color, 2.6f * fade));
            for (int k = 0; k < 6; k++)
            {
                float ang = MathF.PI / 6 + k * MathF.PI / 3;
                float d = cell * MathF.Sqrt(3);
                Hexagon(ctx, center, radius, at, a, b, MathF.Cos(ang) * d, MathF.Sin(ang) * d, cell, Hdr(color, 1.4f * fade * fade));
            }
            ctx.Glow(at, cell * 2.2f, Hdr(color, 1.2f * fade));
        }
    }

    /// <summary>Un hexagone dans le plan tangent (a, b), ramené sur la sphère du bouclier.</summary>
    static void Hexagon(DrawContext ctx, float3 center, float radius, float3 at, float3 a, float3 b, float x, float y, float size, float4 color)
    {
        float3 Corner(int k)
        {
            float ang = k * MathF.PI / 3;
            var p = at + a * (x + MathF.Cos(ang) * size * 0.92f) + b * (y + MathF.Sin(ang) * size * 0.92f);
            return center + float3.NormalizeOrZero(p - center) * radius;
        }
        var first = Corner(0);
        var prev = first;
        for (int k = 1; k <= 6; k++)
        {
            var next = k == 6 ? first : Corner(k);
            ctx.Line(prev, next, color);
            prev = next;
        }
    }

    static void HullHits(Battle battle, DrawContext ctx)
    {
        foreach (var h in battle.HullHits)
        {
            float fade = h.Life / h.MaxLife;
            var (r, k) = Visible(ctx, h.Position, h.Size, 3);
            ctx.Glow(h.Position, r * (1.5f - fade * 0.5f), Hdr(Fire, 1.5f * fade * k));
            ctx.Glow(h.Position, r * 0.5f, Hdr(Flash, 2 * fade * fade * k));
        }
    }

    static void Explosions(Battle battle, DrawContext ctx)
    {
        foreach (var x in battle.Explosions)
        {
            float t = 1 - x.Life / x.MaxLife;
            var (r, k) = Visible(ctx, x.Position, x.Radius * (0.35f + 0.9f * t), 6 + 22 * t);
            k = MathF.Max(k, 0.3f);
            // Boule de feu qui gonfle et s'éteint ; éclair blanc au tout début.
            ctx.Glow(x.Position, r, Hdr(Fire, 1.6f * (1 - t) * (1 - t) * k));
            ctx.Glow(x.Position, r * 0.5f, Hdr(new float4(1f, 0.8f, 0.45f, 1), 1.4f * (1 - t) * k));
            if (t < 0.15f)
                ctx.Glow(x.Position, r * 1.3f, Hdr(Flash, 2.5f * (1 - t / 0.15f) * k));
            // Éclats : douze traits, directions tirées de la position (stables d'une image à l'autre).
            uint seed = (uint)(x.Position.X * 13 + x.Position.Y * 7 + x.Position.Z * 3);
            for (int spark = 0; spark < 12; spark++)
            {
                seed = seed * 1664525 + 1013904223;
                float u = (seed >> 8) / 16777216f;
                seed = seed * 1664525 + 1013904223;
                float v = (seed >> 8) / 16777216f;
                float theta = u * MathF.Tau, z = v * 2 - 1, s = MathF.Sqrt(1 - z * z);
                var d = new float3(s * MathF.Cos(theta), z, s * MathF.Sin(theta));
                var from = x.Position + d * r * 0.6f;
                ctx.Line(from, from + d * r * (0.4f + 0.8f * t), Hdr(Fire, 2.5f * (1 - t)));
            }
        }
    }
}
