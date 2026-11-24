using Brutal.Numerics;
using Poiesis.Engine;

namespace Poiesis.Render;

/// <summary>
/// Formes procédurales unitaires (contenues dans [-0,5 ; 0,5]³, proue vers +Z), mises à l'échelle par instance.
/// Les jeux fournissent leurs propres maillages (générateur, modèles glTF) par la <see cref="MeshLibrary"/>.
/// </summary>
public static class Meshes
{
    public static MeshData Box()
    {
        var m = new MeshData();
        m.Loft(Section(-0.5f, 0.5f, 0.5f), Section(0.5f, 0.5f, 0.5f));
        return m;
    }

    /// <summary>Coque effilée : proue pointue, flancs fuyants, aileron dorsal.</summary>
    public static MeshData Hull()
    {
        var m = new MeshData();
        m.Loft(Section(-0.5f, 0.32f, 0.30f), Section(-0.05f, 0.5f, 0.42f));
        m.Loft(Section(-0.05f, 0.5f, 0.42f), Section(0.30f, 0.34f, 0.30f));
        m.Loft(Section(0.30f, 0.34f, 0.30f), Section(0.5f, 0.04f, 0.06f));
        // Aileron dorsal (passerelle).
        m.Loft(Section(-0.40f, 0.10f, 0.12f, y: 0.27f), Section(-0.10f, 0.06f, 0.10f, y: 0.40f));
        return m;
    }

    /// <summary>Coque massive : blocs empilés, proue plate et blindée.</summary>
    public static MeshData HeavyHull()
    {
        var m = new MeshData();
        m.Loft(Section(-0.5f, 0.46f, 0.36f), Section(0.30f, 0.5f, 0.40f));
        m.Loft(Section(0.30f, 0.5f, 0.40f), Section(0.5f, 0.34f, 0.28f));
        m.Loft(Section(-0.45f, 0.24f, 0.14f, y: 0.27f), Section(0.05f, 0.24f, 0.14f, y: 0.27f));
        m.Loft(Section(-0.35f, 0.12f, 0.12f, y: 0.40f), Section(-0.10f, 0.12f, 0.12f, y: 0.40f));
        return m;
    }

    /// <summary>Sphère facettée (icosphère subdivisée), rayon 0,5.</summary>
    public static MeshData Sphere(int subdivisions = 2)
    {
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var v = new List<float3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        var faces = new List<(int, int, int)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };
        for (int s = 0; s < subdivisions; s++)
        {
            var cache = new Dictionary<(int, int), int>();
            int Mid(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (!cache.TryGetValue(key, out var i))
                {
                    i = v.Count;
                    v.Add((v[a] + v[b]) * 0.5f);
                    cache[key] = i;
                }
                return i;
            }
            var next = new List<(int, int, int)>();
            foreach (var (a, b, c) in faces)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((b, bc, ab)); next.Add((c, ca, bc)); next.Add((ab, bc, ca));
            }
            faces = next;
        }
        var m = new MeshData();
        foreach (var (a, b, c) in faces)
            m.Triangle(float3.Normalize(v[a]) * 0.5f, float3.Normalize(v[b]) * 0.5f, float3.Normalize(v[c]) * 0.5f);
        return m;
    }

    /// <summary>Section rectangulaire à la profondeur <paramref name="z"/>, de demi-largeur et demi-hauteur données.</summary>
    static float3[] Section(float z, float halfWidth, float halfHeight, float y = 0) =>
    [
        new(-halfWidth, y - halfHeight, z),
        new(halfWidth, y - halfHeight, z),
        new(halfWidth, y + halfHeight, z),
        new(-halfWidth, y + halfHeight, z),
    ];
}
