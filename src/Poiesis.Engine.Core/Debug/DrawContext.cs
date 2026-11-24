using Brutal.Numerics;

namespace Poiesis.Engine.Debug;

/// <summary>
/// Ce qu'un jeu peut dessiner par-dessus le monde dans le viewport de l'éditeur : traits (tirs, rayons, portées),
/// étiquettes ancrées dans le monde et lignes de HUD. Les dessins « de débogage » appartiennent à des canaux
/// que l'éditeur permet d'allumer et d'éteindre.
/// </summary>
public sealed class DrawContext
{
    public readonly record struct LineItem(float3 A, float3 B, float4 Color);
    public readonly record struct LabelItem(float3 At, string Text, float4 Color);

    readonly List<LineItem> _lines = new();
    readonly List<LabelItem> _labels = new();
    readonly List<(string Text, float4 Color)> _hud = new();

    public HashSet<string> EnabledChannels { get; } = new(StringComparer.Ordinal);

    /// <summary>Position de la caméra (pour adapter les tailles au zoom).</summary>
    public float3 CameraPosition { get; set; }

    /// <summary>Pixels par radian à l'écran : une longueur L à la distance D fait L / D × <c>PixelsPerRadian</c> pixels.</summary>
    public float PixelsPerRadian { get; set; } = 1000;

    /// <summary>Entité sélectionnée dans l'éditeur (slot), ou -1.</summary>
    public int SelectedEntity { get; set; } = -1;

    public IReadOnlyList<LineItem> Lines => _lines;
    public IReadOnlyList<LabelItem> Labels => _labels;
    public IReadOnlyList<(string Text, float4 Color)> Hud => _hud;

    public bool IsEnabled(string channel) => EnabledChannels.Contains(channel);

    public void Line(float3 a, float3 b, float4 color) => _lines.Add(new LineItem(a, b, color));

    /// <summary>Cercle dans le plan horizontal (portées).</summary>
    public void Circle(float3 center, float radius, float4 color, int segments = 48)
    {
        var prev = center + new float3(radius, 0, 0);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var p = center + new float3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius);
            Line(prev, p, color);
            prev = p;
        }
    }

    public void Label(float3 at, string text, float4 color) => _labels.Add(new LabelItem(at, text, color));

    public void HudLine(string text, float4 color) => _hud.Add((text, color));

    /// <summary>Taille en mètres qui fait <paramref name="pixels"/> pixels à l'écran à la position donnée.</summary>
    public float MetersForPixels(float3 at, float pixels) =>
        pixels * MathF.Max(float3.Distance(at, CameraPosition), 1f) / PixelsPerRadian;

    public void Clear()
    {
        _lines.Clear();
        _labels.Clear();
        _hud.Clear();
    }
}
