using Brutal.Numerics;
using Poiesis.Engine;

namespace Orionum.Game;

/// <summary>
/// Le profil d'une coque générée : demi-largeur et demi-hauteur du corps le long de l'axe (poupe à -L/2, proue à +L/2),
/// en mètres. Sert à poser les tourelles sur la coque.
/// </summary>
public sealed class HullProfile(IReadOnlyList<(float Z, float HalfWidth, float HalfHeight)> rings, float length)
{
    public float Length { get; } = length;

    public IReadOnlyList<(float Z, float HalfWidth, float HalfHeight)> Rings { get; } = rings;

    public float HalfWidth(float z) => Sample(z).W;

    public float HalfHeight(float z) => Sample(z).H;

    (float W, float H) Sample(float z)
    {
        if (Rings.Count == 0 || z < Rings[0].Z || z > Rings[^1].Z)
            return (0, 0);
        for (int i = 1; i < Rings.Count; i++)
            if (z <= Rings[i].Z)
            {
                var (z0, w0, h0) = Rings[i - 1];
                var (z1, w1, h1) = Rings[i];
                float t = z1 - z0 > 1e-4f ? (z - z0) / (z1 - z0) : 1;
                return (w0 + (w1 - w0) * t, h0 + (h1 - h0) * t);
            }
        return (Rings[^1].HalfWidth, Rings[^1].HalfHeight);
    }
}

/// <summary>Une coque générée : son maillage (en mètres, proue vers +Z, texturé) et son profil.</summary>
public sealed record GeneratedHull(MeshData Mesh, HullProfile Profile);

/// <summary>
/// Le générateur de coques d'Orionum : un corps en segments à section octogonale, une proue (effilée, en lame, massive
/// ou fourchue), des superstructures en étages, des tuyères et une quille. Facettes plates, comme les formes de
/// l'éditeur, et coordonnées de texture en projection par face pour le blindage.
/// </summary>
public static class HullGenerator
{
    const float Chamfer = 0.35f;

    public static GeneratedHull Build(HullDesign d, float length)
    {
        var mesh = new MeshData();
        float L = MathF.Max(length, 1);
        float tile = L / 10;   // une plaque de la texture pour un dixième de la longueur
        float a = Clamp(d.Beam, 0.03f, 0.8f) * L / 2;
        float b = Clamp(d.Height, 0.02f, 0.6f) * L / 2;
        float bow = Clamp(d.BowLength, 0.05f, 0.6f) * L;
        float zStern = -L / 2, zBow = L / 2 - bow;
        int sections = Math.Clamp(d.Sections, 1, 8);
        float waist = Clamp(d.Waist, 0, 0.6f);
        float taper = Clamp(d.SternTaper, 0, 0.8f);

        // Le corps : poupe, segments pincés aux jointures, début de la proue.
        var body = new List<(float Z, float W, float H)> { (zStern, a * (1 - taper), b * (1 - taper * 0.6f)) };
        float seg = (zBow - zStern) / sections;
        for (int i = 0; i < sections; i++)
        {
            float s0 = zStern + i * seg, s1 = s0 + seg;
            body.Add((s0 + 0.15f * seg, a, b));
            body.Add((s1 - 0.15f * seg, a, b));
            if (i < sections - 1)
                body.Add((s1, a * (1 - waist), b * (1 - waist * 0.7f)));
        }
        body.Add((zBow, a, b));

        bool split = d.Bow == BowStyle.Split;
        switch (d.Bow)
        {
            case BowStyle.Pointed:
                body.Add((zBow + bow * 0.5f, a * 0.62f, b * 0.72f));
                body.Add((L / 2, a * 0.04f, b * 0.1f));
                break;
            case BowStyle.Wedge:
                body.Add((zBow + bow * 0.55f, a * 0.95f, b * 0.5f));
                body.Add((L / 2, a * 0.8f, b * 0.08f));
                break;
            case BowStyle.Blunt:
                body.Add((zBow + bow * 0.6f, a * 0.97f, b * 0.95f));
                body.Add((L / 2, a * 0.78f, b * 0.72f));
                break;
            case BowStyle.Split:
                body.Add((zBow + bow * 0.25f, a * 0.92f, b * 0.9f));
                break;
        }

        var rings = body.Select(r => Ring(new float3(0, 0, r.Z), r.W, r.H)).ToList();
        for (int i = 1; i < rings.Count; i++)
            LoftRing(mesh, rings[i - 1], rings[i], tile);
        Cap(mesh, rings[0], reverse: true, tile);
        Cap(mesh, rings[^1], reverse: false, tile);

        // Proue fourchue : deux pointes de part et d'autre de l'axe.
        if (split)
        {
            float z0 = body[^1].Z;
            foreach (var side in new[] { -1f, 1f })
            {
                float x = side * a * 0.55f;
                var prong = new[]
                {
                    Ring(new float3(x, 0, z0), a * 0.36f, b * 0.8f),
                    Ring(new float3(x, 0, z0 + (L / 2 - z0) * 0.55f), a * 0.28f, b * 0.6f),
                    Ring(new float3(x + side * a * 0.05f, 0, L / 2), a * 0.04f, b * 0.12f),
                };
                for (int i = 1; i < prong.Length; i++)
                    LoftRing(mesh, prong[i - 1], prong[i], tile);
                Cap(mesh, prong[^1], reverse: false, tile);
            }
        }

        var profile = new HullProfile(body.Select(r => (r.Z, r.W, r.H)).ToList(), L);

        // Superstructures : des blocs en étages posés sur le pont.
        foreach (var s in d.Superstructures)
        {
            float z = Clamp(s.At, -0.5f, 0.5f) * L;
            float baseY = profile.HalfHeight(z) * 0.92f;
            if (baseY <= 0)
                continue;
            int tiers = Math.Clamp(s.Tiers, 1, 5);
            float tierHeight = MathF.Max(s.Height, 0.005f) * L / tiers;
            for (int k = 0; k < tiers; k++)
            {
                float w = MathF.Max(s.Width, 0.005f) * L / 2 * (1 - 0.22f * k);
                float l = MathF.Max(s.Length, 0.005f) * L / 2 * (1 - 0.25f * k);
                float y = baseY + tierHeight * (k + 0.5f);
                var back = Ring(new float3(0, y, z - l), w, tierHeight / 2, chamfer: 0.15f);
                var front = Ring(new float3(0, y, z + l * 0.9f), w * 0.85f, tierHeight / 2, chamfer: 0.15f);
                LoftRing(mesh, back, front, tile);
                Cap(mesh, back, reverse: true, tile);
                Cap(mesh, front, reverse: false, tile);
            }
        }

        // Tuyères à la poupe, en ligne sur la largeur.
        int engines = Math.Clamp(d.Engines, 0, 8);
        float er = Clamp(d.EngineSize, 0.005f, 0.2f) * L / 2;
        float sternWidth = a * (1 - taper);
        for (int i = 0; i < engines; i++)
        {
            float x = engines == 1 ? 0 : -sternWidth * 0.6f + 1.2f * sternWidth * i / (engines - 1);
            var inner = Ring(new float3(x, 0, zStern + L * 0.01f), er, er, chamfer: 0.29f);
            var outer = Ring(new float3(x, 0, zStern - L * 0.05f), er * 1.2f, er * 1.2f, chamfer: 0.29f);
            LoftRing(mesh, outer, inner, tile);
            Cap(mesh, outer, reverse: true, tile);
        }

        // Quille ventrale.
        if (d.Keel > 0.001f)
        {
            float depth = d.Keel * L;
            float z0 = zStern + (zBow - zStern) * 0.15f, z1 = zBow;
            float bottom0 = -profile.HalfHeight(z0) * 0.9f, bottom1 = -profile.HalfHeight(z1) * 0.9f;
            float t = L * 0.006f;
            mesh.Loft(
                [new(-t, bottom0 - depth, z0), new(t, bottom0 - depth, z0), new(t, bottom0, z0), new(-t, bottom0, z0)],
                [new(-t, bottom1 - depth * 0.3f, z1), new(t, bottom1 - depth * 0.3f, z1), new(t, bottom1, z1), new(-t, bottom1, z1)]);
            FillTexCoords(mesh, tile);
        }

        return new GeneratedHull(mesh, profile);
    }

    static float Clamp(float v, float min, float max) => float.IsNaN(v) ? min : Math.Clamp(v, min, max);

    /// <summary>Section octogonale (rectangle chanfreiné) centrée sur <paramref name="c"/>.</summary>
    static float3[] Ring(float3 c, float a, float b, float chamfer = Chamfer)
    {
        float k = 1 - chamfer;
        return
        [
            c + new float3(a, b * k, 0), c + new float3(a * k, b, 0), c + new float3(-a * k, b, 0), c + new float3(-a, b * k, 0),
            c + new float3(-a, -b * k, 0), c + new float3(-a * k, -b, 0), c + new float3(a * k, -b, 0), c + new float3(a, -b * k, 0),
        ];
    }

    static void LoftRing(MeshData mesh, float3[] back, float3[] front, float tile)
    {
        int n = back.Length;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            // Sens choisi pour que la normale pointe vers l'extérieur.
            Quad(mesh, back[i], back[j], front[j], front[i], tile);
        }
    }

    static void Cap(MeshData mesh, float3[] ring, bool reverse, float tile)
    {
        var c = ring.Aggregate(float3.Zero, (acc, p) => acc + p) / ring.Length;
        for (int i = 0; i < ring.Length; i++)
        {
            int j = (i + 1) % ring.Length;
            if (reverse)
                Triangle(mesh, c, ring[j], ring[i], tile);   // vers -Z (poupe)
            else
                Triangle(mesh, c, ring[i], ring[j], tile);   // vers +Z (proue)
        }
    }

    static void Quad(MeshData mesh, float3 a, float3 b, float3 c, float3 d, float tile)
    {
        Triangle(mesh, a, b, c, tile);
        Triangle(mesh, a, c, d, tile);
    }

    static void Triangle(MeshData mesh, float3 a, float3 b, float3 c, float tile)
    {
        if (float3.Cross(b - a, c - a).LengthSquared() < 1e-8f)
            return;   // triangle dégénéré (pointe de proue)
        mesh.Triangle(a, b, c);
        FillTexCoords(mesh, tile);
    }

    /// <summary>Coordonnées de texture des sommets qui n'en ont pas : projection sur le plan le plus proche de la facette.</summary>
    static void FillTexCoords(MeshData mesh, float tile)
    {
        for (int i = mesh.TexCoords.Count; i < mesh.Positions.Count; i++)
        {
            var p = mesh.Positions[i] / tile;
            var n = mesh.Normals[i];
            float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
            mesh.TexCoords.Add(ax >= ay && ax >= az ? new float2(p.Z, p.Y) : ay >= az ? new float2(p.Z, p.X) : new float2(p.X, p.Y));
        }
    }

    /// <summary>
    /// La texture de blindage des coques générées (grise, teintée par la couleur de la faction) : plaques de tons
    /// légèrement différents, joints sombres, rangées de hublots.
    /// </summary>
    public static TextureData Plating(int size = 256)
    {
        var rgba = new byte[size * size * 4];
        int panel = size / 8;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int px = x / panel, py = y / (panel / 2);
                uint h = (uint)(px * 73856093) ^ (uint)(py * 19349663);
                h = (h ^ (h >> 13)) * 0x5bd1e995;
                float tone = 0.78f + 0.14f * ((h >> 8) & 0xff) / 255f;
                bool seam = x % panel == 0 || y % (panel / 2) == 0;
                bool window = (y % (panel / 2)) == panel / 4 && (x % 6) < 2 && ((h >> 3) & 3) == 0;
                float v = window ? 1.0f : seam ? tone * 0.55f : tone;
                int i = (y * size + x) * 4;
                byte c = (byte)(Math.Clamp(v, 0, 1) * 255);
                rgba[i] = window ? (byte)255 : c;
                rgba[i + 1] = window ? (byte)236 : c;
                rgba[i + 2] = window ? (byte)180 : c;
                rgba[i + 3] = 255;
            }
        return new TextureData(size, size, rgba);
    }

    /// <summary>
    /// Pose les tourelles sur la coque : bordées sur les flancs (bâbord +X, tribord -X), tourelles sur le pont,
    /// défense ponctuelle répartie sur l'enveloppe. Rend la liste des armes avec leurs emplacements recalculés.
    /// </summary>
    public static List<WeaponDef> SnapHardpoints(IEnumerable<WeaponDef> weapons, HullProfile profile)
    {
        float half = profile.Length * 0.45f;
        var result = new List<WeaponDef>();
        foreach (var w in weapons)
        {
            var hardpoints = new List<HardpointSpec>();
            foreach (var h in w.Hardpoints)
            {
                if (h.Row is [var n, var x, var y, var z0, var z1])
                {
                    z0 = Math.Clamp(z0, -half, half);
                    z1 = Math.Clamp(z1, -half, half);
                    float hw = MathF.Min(profile.HalfWidth(z0), profile.HalfWidth(z1));
                    float hh = MathF.Min(profile.HalfHeight(z0), profile.HalfHeight(z1));
                    (x, y) = w.Mount switch
                    {
                        Mount.Port => (hw, Math.Clamp(y, -hh * 0.6f, hh * 0.6f)),
                        Mount.Starboard => (-hw, Math.Clamp(y, -hh * 0.6f, hh * 0.6f)),
                        Mount.Turret => (Math.Clamp(x, -hw * 0.6f, hw * 0.6f), hh),
                        _ => (x, Math.Clamp(y, -hh, hh)),
                    };
                    hardpoints.Add(h with { Row = [n, Round(x), Round(y), Round(z0), Round(z1)] });
                }
                else if (h.Scatter is [var sn, _, _, var hl])
                {
                    hl = MathF.Min(hl, half);
                    hardpoints.Add(h with { Scatter = [sn, Round(profile.HalfWidth(0)), Round(profile.HalfHeight(0)), Round(hl)] });
                }
                else if (h.Point is { } p)
                {
                    float z = Math.Clamp(p.Z, -half, profile.Length / 2);
                    var snapped = w.Mount switch
                    {
                        Mount.Port => new float3(profile.HalfWidth(z), p.Y, z),
                        Mount.Starboard => new float3(-profile.HalfWidth(z), p.Y, z),
                        Mount.Turret => new float3(p.X, profile.HalfHeight(z), z),
                        _ => new float3(p.X, p.Y, z),
                    };
                    hardpoints.Add(h with { Point = new float3(Round(snapped.X), Round(snapped.Y), Round(snapped.Z)) });
                }
                else
                {
                    hardpoints.Add(h);
                }
            }
            result.Add(w with { Hardpoints = hardpoints });
        }
        return result;
    }

    static float Round(float v) => MathF.Round(v, 1);
}
