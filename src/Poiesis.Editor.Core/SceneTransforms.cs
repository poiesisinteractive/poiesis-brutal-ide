using System.Reflection;
using Brutal.Numerics;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor;

/// <summary>
/// Ce que les gizmos et le menu Disposer savent faire des nœuds, par convention de nommage : <c>Position</c> (float3),
/// <c>Heading</c> (degrés autour de la verticale) et la propriété marquée <see cref="ScaleHandleAttribute"/>.
/// </summary>
public static class SceneTransforms
{
    public static PropertyInfo? PositionProperty(Type t) =>
        t.GetProperty("Position") is { CanWrite: true } p && p.PropertyType == typeof(float3) ? p : null;

    public static PropertyInfo? HeadingProperty(Type t) =>
        t.GetProperty("Heading") is { CanWrite: true } p && p.PropertyType == typeof(float) ? p : null;

    public static PropertyInfo? ScaleProperty(Type t) =>
        SceneSerializer.EditableProperties(t).FirstOrDefault(p => p.GetCustomAttribute<ScaleHandleAttribute>() is not null
            && (p.PropertyType == typeof(float) || p.PropertyType == typeof(int)));

    public static float3? PositionOf(Node n) => PositionProperty(n.GetType())?.GetValue(n) is float3 v ? v : null;

    /// <summary>Les nœuds qui ont une position, dans l'ordre donné.</summary>
    public static IReadOnlyList<Node> Movable(IEnumerable<Node> nodes) => nodes.Where(n => PositionProperty(n.GetType()) is not null).ToList();

    /// <summary>Le centre d'un groupe : la moyenne des positions.</summary>
    public static float3 Pivot(IReadOnlyList<Node> nodes)
    {
        var movable = Movable(nodes);
        return movable.Count == 0 ? float3.Zero : movable.Aggregate(float3.Zero, (acc, n) => acc + PositionOf(n)!.Value) / movable.Count;
    }

    static float Axis(float3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    static float3 WithAxis(float3 v, int axis, float value) => axis switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    /// <summary>Aligne la coordonnée <paramref name="axis"/> (0 X, 1 Y, 2 Z) de chaque nœud sur celle de <paramref name="reference"/>.</summary>
    public static IEditorCommand? Align(IReadOnlyList<Node> nodes, int axis, Node reference)
    {
        if (PositionOf(reference) is not { } r)
            return null;
        var commands = new List<IEditorCommand>();
        foreach (var n in Movable(nodes))
            if (n != reference && PositionOf(n) is { } p && MathF.Abs(Axis(p, axis) - Axis(r, axis)) > 1e-3f)
                commands.Add(new SetPropertyCommand(n, PositionProperty(n.GetType())!, WithAxis(p, axis, Axis(r, axis))));
        return commands.Count == 0 ? null : new CompositeCommand($"Aligner sur {"XYZ"[axis]}", commands);
    }

    /// <summary>Répartit les nœuds à intervalles égaux le long d'un axe, entre les deux extrêmes (qui ne bougent pas).</summary>
    public static IEditorCommand? Distribute(IReadOnlyList<Node> nodes, int axis)
    {
        var movable = Movable(nodes).OrderBy(n => Axis(PositionOf(n)!.Value, axis)).ToList();
        if (movable.Count < 3)
            return null;
        float min = Axis(PositionOf(movable[0])!.Value, axis), max = Axis(PositionOf(movable[^1])!.Value, axis);
        var commands = new List<IEditorCommand>();
        for (int i = 1; i < movable.Count - 1; i++)
        {
            var p = PositionOf(movable[i])!.Value;
            float target = MathF.Round(min + (max - min) * i / (movable.Count - 1));
            if (MathF.Abs(Axis(p, axis) - target) > 1e-3f)
                commands.Add(new SetPropertyCommand(movable[i], PositionProperty(movable[i].GetType())!, WithAxis(p, axis, target)));
        }
        return commands.Count == 0 ? null : new CompositeCommand($"Répartir sur {"XYZ"[axis]}", commands);
    }

    /// <summary>Donne à chaque nœud le cap de <paramref name="reference"/>.</summary>
    public static IEditorCommand? AlignHeadings(IReadOnlyList<Node> nodes, Node reference)
    {
        if (HeadingProperty(reference.GetType())?.GetValue(reference) is not float h)
            return null;
        var commands = nodes.Where(n => n != reference && HeadingProperty(n.GetType()) is { } p && (float)p.GetValue(n)! != h)
            .Select(n => (IEditorCommand)new SetPropertyCommand(n, HeadingProperty(n.GetType())!, h)).ToList();
        return commands.Count == 0 ? null : new CompositeCommand("Aligner les caps", commands);
    }
}

/// <summary>
/// Un glissement de gizmo sur un groupe : les valeurs de départ sont gardées, les changements s'appliquent en direct,
/// puis <see cref="Commit"/> remet les valeurs de départ et rend une seule commande annulable avec les valeurs finales
/// (arrondies : au mètre, au degré, au centième).
/// </summary>
public sealed class TransformDrag
{
    sealed record Start(Node Node, PropertyInfo? Position, float3 Pos, PropertyInfo? Heading, float Head, PropertyInfo? Scale, float ScaleValue);

    readonly List<Start> _starts;

    public TransformDrag(IEnumerable<Node> nodes)
    {
        _starts = nodes.Select(n =>
        {
            var t = n.GetType();
            var pos = SceneTransforms.PositionProperty(t);
            var head = SceneTransforms.HeadingProperty(t);
            var scale = SceneTransforms.ScaleProperty(t);
            return new Start(n, pos, pos?.GetValue(n) is float3 p ? p : default, head, head?.GetValue(n) is float h ? h : 0,
                scale, scale?.GetValue(n) is { } s ? Convert.ToSingle(s) : 0);
        }).ToList();
        var movable = _starts.Where(s => s.Position is not null).ToList();
        Pivot = movable.Count == 0 ? float3.Zero : movable.Aggregate(float3.Zero, (acc, s) => acc + s.Pos) / movable.Count;
    }

    public float3 Pivot { get; }

    public int Count => _starts.Count;

    /// <summary>Déplace tout le groupe de <paramref name="offset"/>.</summary>
    public void Translate(float3 offset)
    {
        foreach (var s in _starts)
            s.Position?.SetValue(s.Node, s.Pos + offset);
    }

    /// <summary>Tourne le groupe autour de son centre (verticale) et chaque cap de <paramref name="degrees"/>.</summary>
    public void Rotate(float degrees)
    {
        float r = degrees * MathF.PI / 180, c = MathF.Cos(r), sn = MathF.Sin(r);
        foreach (var s in _starts)
        {
            s.Heading?.SetValue(s.Node, s.Head + degrees);
            if (s.Position is not null && _starts.Count > 1)
            {
                // Le cap tourne de +X vers +Z (0° = +Z, 90° = +X) : la même rotation pour les positions.
                var d = s.Pos - Pivot;
                s.Position.SetValue(s.Node, Pivot + new float3(d.X * c + d.Z * sn, d.Y, -d.X * sn + d.Z * c));
            }
        }
    }

    /// <summary>
    /// Met le groupe à l'échelle : la propriété d'échelle de chacun (bornée par son <see cref="RangeAttribute"/>), et,
    /// pour un groupe, les distances au centre.
    /// </summary>
    public void Scale(float factor)
    {
        factor = Math.Clamp(factor, 0.01f, 100f);
        foreach (var s in _starts)
        {
            if (s.Scale is { } p)
            {
                float v = s.ScaleValue * factor;
                if (p.GetCustomAttribute<RangeAttribute>() is { } range)
                    v = Math.Clamp(v, range.Min, range.Max);
                p.SetValue(s.Node, p.PropertyType == typeof(int) ? (object)(int)MathF.Round(v) : v);
            }
            if (s.Position is not null && _starts.Count > 1)
                s.Position.SetValue(s.Node, Pivot + (s.Pos - Pivot) * factor);
        }
    }

    /// <summary>Remet les valeurs de départ et rend la commande des valeurs finales, ou <c>null</c> si rien n'a changé.</summary>
    public IEditorCommand? Commit(string label)
    {
        var commands = new List<IEditorCommand>();
        foreach (var s in _starts)
        {
            if (s.Position is { } pos && pos.GetValue(s.Node) is float3 p)
            {
                pos.SetValue(s.Node, s.Pos);
                var final = new float3(MathF.Round(p.X) + 0f, MathF.Round(p.Y) + 0f, MathF.Round(p.Z) + 0f);   // + 0 : pas de « -0 » dans les fichiers
                if (float3.Distance(final, s.Pos) > 1e-3f)
                    commands.Add(new SetPropertyCommand(s.Node, pos, final));
            }
            if (s.Heading is { } head && head.GetValue(s.Node) is float h)
            {
                head.SetValue(s.Node, s.Head);
                var final = MathF.Round(h, 1);
                if (MathF.Abs(final - s.Head) > 1e-3f)
                    commands.Add(new SetPropertyCommand(s.Node, head, final));
            }
            if (s.Scale is { } scale && scale.GetValue(s.Node) is { } v)
            {
                var start = scale.PropertyType == typeof(int) ? (object)(int)s.ScaleValue : s.ScaleValue;
                scale.SetValue(s.Node, start);
                var final = v is float f ? (object)MathF.Round(f, 2) : v;
                if (!Equals(final, start))
                    commands.Add(new SetPropertyCommand(s.Node, scale, final));
            }
        }
        return commands.Count == 0 ? null : commands.Count == 1 ? commands[0] : new CompositeCommand(label, commands);
    }

    /// <summary>Abandonne : remet les valeurs de départ.</summary>
    public void Cancel() => Commit("");
}
