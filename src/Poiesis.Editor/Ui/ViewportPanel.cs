using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;
using Poiesis.Render;
using Poiesis.Render.Vulkan;

namespace Poiesis.Editor.Ui;

/// <summary>
/// La vue 3D de l'éditeur : la <see cref="WorldView"/> commune avec le joueur (caméra, entités, dessins du jeu),
/// plus ce qui est propre à l'édition : grille, sélection au clic, gizmo de translation, anneau de cap.
/// En édition, on voit l'aperçu cuit de la scène ; en jeu, le monde de la partie.
/// </summary>
sealed class ViewportPanel
{
    readonly EditorSession _session;
    readonly SceneRenderer _renderer;
    readonly ViewportTextures _textures;
    readonly WorldView _view;
    bool _framedOnce;

    // Gizmo de translation : axe saisi (-1 = aucun), nœud et valeur de départ.
    int _dragAxis = -1;
    Node? _dragNode;
    PropertyInfo? _dragProperty;
    float3 _dragStart;
    float3 _dragCurrent;

    // Anneau de rotation : cap (propriété « Heading », degrés) du nœud sélectionné.
    bool _ringDrag;
    Node? _ringNode;
    PropertyInfo? _ringProperty;
    float _ringStart, _ringCurrent, _ringLastAngle;

    static readonly float3[] Axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    static readonly float4[] AxisColors = [new(0.95f, 0.30f, 0.30f, 1), new(0.40f, 0.90f, 0.35f, 1), new(0.35f, 0.55f, 1f, 1)];

    /// <summary>Les canaux de débogage allumés dans le panneau Débogage.</summary>
    public HashSet<string>? DebugChannels { get; set; }

    /// <summary>Gizmos fournis par le jeu (API d'extension).</summary>
    public ExtensionHost? Extensions { get; set; }

    public float GridStep { get; private set; }

    public ViewportPanel(EditorSession session, SceneRenderer renderer, ViewportTextures textures)
    {
        _session = session;
        _renderer = renderer;
        _textures = textures;
        _view = new WorldView(renderer);
    }

    /// <summary>Le monde affiché et la correspondance entité → nœud.</summary>
    (World? World, BakeContext? Bake) Source() =>
        _session.Play is { } play ? (play.World, play.Bake) : (_session.Preview, _session.PreviewBake);

    public void Draw(int slot)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 0));
        bool open = ImGui.Begin("Viewport"u8);
        ImGui.PopStyleVar();
        if (!open)
        {
            ImGui.End();
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var size = new int2(Math.Max((int)avail.X, 1), Math.Max((int)avail.Y, 1));
        _renderer.Resize(size);
        _view.ImageMin = ImGui.GetCursorScreenPos();
        _view.ImageSize = new float2(size.X, size.Y);

        if (!_framedOnce && Source().World is { } w && w.EntityCount > 0)
        {
            _view.FrameAll(w);
            _framedOnce = true;
        }

        ImGui.Image(_textures.Viewport(slot), _view.ImageSize);
        bool hovered = ImGui.IsItemHovered();
        // Les poignées des gizmos du jeu passent avant la caméra et la sélection.
        bool consumed = Extensions?.DrawGizmos(_view, hovered) ?? false;
        HandleInput(hovered && !consumed);
        DrawOverlay();
        ImGui.End();
    }

    /// <summary>Prépare l'image 3D de cette frame (à appeler après <see cref="Draw"/>, avant la passe de la fenêtre).</summary>
    public RenderFrame BuildFrame()
    {
        _view.Begin();
        DrawGrid();
        var (world, bake) = Source();
        if (world is not null)
        {
            var selected = SelectedSubtree();
            _view.DrawEntities(world, selected.Count == 0 || bake is null ? null : e => bake.OwnerOf.TryGetValue(e, out var owner) && selected.Contains(owner));
        }
        DrawGizmo();
        DrawRing();
        if (Extensions is not null)
            foreach (var l in Extensions.GizmoDraw.Lines)
                _view.Frame.Line(l.A, l.B, l.Color);
        if (world is not null)
            _view.DrawGame(world, _session.Modules.Module as IWorldVisualizer, DebugChannels, _session.Play is not null ? _session.SelectedEntity : -1);
        return _view.Frame;
    }

    readonly HashSet<Guid> _selectedIds = new();

    /// <summary>Le nœud sélectionné et ses descendants, calculé une fois par image (pas une fois par entité).</summary>
    HashSet<Guid> SelectedSubtree()
    {
        _selectedIds.Clear();
        if (_session.Selection is { } node)
            foreach (var n in node.DescendantsAndSelf())
                _selectedIds.Add(n.Id);
        return _selectedIds;
    }

    void DrawGrid()
    {
        // Pas de grille adapté au zoom (puissance de 10), lignes majeures tous les 10 pas.
        float step = MathF.Pow(10, MathF.Floor(MathF.Log10(MathF.Max(_view.Distance / 4f, 10f))));
        // Étendue bornée à ~1,5 fois la distance de vue : des lignes bien plus longues que la distance à la caméra
        // perdent leur précision au découpage (artefact constaté à 500 km).
        int half = Math.Clamp((int)MathF.Ceiling(1.5f * _view.Distance / step), 4, 20);
        float cx = MathF.Round(_view.Target.X / step) * step, cz = MathF.Round(_view.Target.Z / step) * step;
        var minor = new float4(0.30f, 0.32f, 0.42f, 0.35f);
        var major = new float4(0.45f, 0.48f, 0.62f, 0.70f);
        for (int i = -half; i <= half; i++)
        {
            float x = cx + i * step, z = cz + i * step;
            var c = MathF.Abs(MathF.IEEERemainder(x, step * 10)) < step * 0.5f ? major : minor;
            _view.Frame.Line(new float3(x, 0, cz - half * step), new float3(x, 0, cz + half * step), c);
            c = MathF.Abs(MathF.IEEERemainder(z, step * 10)) < step * 0.5f ? major : minor;
            _view.Frame.Line(new float3(cx - half * step, 0, z), new float3(cx + half * step, 0, z), c);
        }
        GridStep = step;
    }

    // ----- Gizmo de translation ----------------------------------------------------------------------

    /// <summary>La position éditable du nœud sélectionné : sa propriété <c>Position</c> (float3), s'il en a une.</summary>
    (Node Node, PropertyInfo Property)? GizmoTarget()
    {
        if (_session.Selection is not { } node || _session.Play is not null)
            return null;
        var p = node.GetType().GetProperty("Position");
        return p is not null && p.PropertyType == typeof(float3) && p.CanWrite ? (node, p) : null;
    }

    float GizmoLength(float3 at) => float3.Distance(_view.EyePosition(), at) * 0.15f;

    void DrawGizmo()
    {
        if (GizmoTarget() is not { } g)
            return;
        var pos = (float3)g.Property.GetValue(g.Node)!;
        var len = GizmoLength(pos);
        for (int a = 0; a < 3; a++)
        {
            var c = _dragAxis == a ? new float4(1, 1, 0.4f, 1) : AxisColors[a];
            _view.Frame.Line(pos, pos + Axes[a] * len, c);
            var tip = pos + Axes[a] * len;
            var side = Axes[(a + 1) % 3] * len * 0.06f;
            _view.Frame.Line(tip, tip - Axes[a] * len * 0.15f + side, c);
            _view.Frame.Line(tip, tip - Axes[a] * len * 0.15f - side, c);
        }
    }

    bool TryStartGizmoDrag(float2 mouse)
    {
        if (GizmoTarget() is not { } g)
            return false;
        var pos = (float3)g.Property.GetValue(g.Node)!;
        var len = GizmoLength(pos);
        if (!_view.Project(pos, out var s0))
            return false;
        int best = -1;
        float bestDist = 10f;   // pixels
        for (int a = 0; a < 3; a++)
        {
            if (!_view.Project(pos + Axes[a] * len, out var s1))
                continue;
            var d = WorldView.DistanceToSegment(mouse, s0, s1);
            if (d < bestDist)
                (best, bestDist) = (a, d);
        }
        if (best < 0)
            return false;
        _dragAxis = best;
        _dragNode = g.Node;
        _dragProperty = g.Property;
        _dragStart = _dragCurrent = pos;
        return true;
    }

    void ContinueGizmoDrag(float2 mouseDelta)
    {
        var axis = Axes[_dragAxis];
        var len = GizmoLength(_dragCurrent);
        if (!_view.Project(_dragCurrent, out var s0) || !_view.Project(_dragCurrent + axis * len, out var s1))
            return;
        var dir = s1 - s0;
        var lenSq = float2.Dot(dir, dir);
        if (lenSq < 1e-3f)
            return;
        _dragCurrent += axis * (float2.Dot(mouseDelta, dir) / lenSq * len);
        _dragProperty!.SetValue(_dragNode, _dragCurrent);
        _session.Touch();
    }

    void EndGizmoDrag()
    {
        // La valeur finale devient une commande annulable, comme une modification dans l'inspecteur.
        _dragProperty!.SetValue(_dragNode, _dragStart);
        if (float3.Distance(_dragStart, _dragCurrent) > 1e-3f)
            _session.History.Execute(new SetPropertyCommand(_dragNode!, _dragProperty, Snap(_dragCurrent)));
        _dragAxis = -1;
        _dragNode = null;
        _dragProperty = null;
    }

    /// <summary>Arrondi au mètre : les fichiers de scène restent lisibles.</summary>
    static float3 Snap(float3 v) => new(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z));

    // ----- Anneau de cap -----------------------------------------------------------------------------

    /// <summary>La propriété de cap (« Heading », degrés autour de la verticale) du nœud sélectionné, s'il en a une.</summary>
    (Node Node, PropertyInfo Property)? RingTarget()
    {
        if (_session.Selection is not { } node || _session.Play is not null)
            return null;
        var p = node.GetType().GetProperty("Heading");
        var pos = node.GetType().GetProperty("Position");
        return p is not null && p.PropertyType == typeof(float) && p.CanWrite && pos?.PropertyType == typeof(float3) ? (node, p) : null;
    }

    static float3 RingCenter(Node node) => (float3)node.GetType().GetProperty("Position")!.GetValue(node)!;

    void DrawRing()
    {
        if (RingTarget() is not { } g)
            return;
        var center = RingCenter(g.Node);
        float r = GizmoLength(center) * 0.75f;
        var color = _ringDrag ? new float4(1, 1, 0.4f, 1) : new float4(1f, 0.8f, 0.3f, 0.9f);
        const int n = 64;
        for (int i = 0; i < n; i++)
        {
            float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
            _view.Frame.Line(center + new float3(MathF.Sin(a0), 0, MathF.Cos(a0)) * r, center + new float3(MathF.Sin(a1), 0, MathF.Cos(a1)) * r, color);
        }
        float yaw = (float)g.Property.GetValue(g.Node)! * MathF.PI / 180;
        _view.Frame.Line(center, center + new float3(MathF.Sin(yaw), 0, MathF.Cos(yaw)) * r, color);
    }

    /// <summary>Angle (degrés, convention du cap) du point visé par la souris dans le plan horizontal du nœud.</summary>
    bool MouseHeading(float2 mouse, float3 center, out float degrees)
    {
        degrees = 0;
        if (!_view.Ray(mouse, out var origin, out var dir) || MathF.Abs(dir.Y) < 1e-4f)
            return false;
        float t = (center.Y - origin.Y) / dir.Y;
        if (t <= 0)
            return false;
        var hit = origin + dir * t - center;
        degrees = MathF.Atan2(hit.X, hit.Z) * 180 / MathF.PI;
        return true;
    }

    bool TryStartRingDrag(float2 mouse)
    {
        if (RingTarget() is not { } g)
            return false;
        var center = RingCenter(g.Node);
        float r = GizmoLength(center) * 0.75f;
        const int n = 64;
        bool near = false;
        for (int i = 0; i < n && !near; i++)
        {
            float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
            if (_view.Project(center + new float3(MathF.Sin(a0), 0, MathF.Cos(a0)) * r, out var s0)
                && _view.Project(center + new float3(MathF.Sin(a1), 0, MathF.Cos(a1)) * r, out var s1))
                near = WorldView.DistanceToSegment(mouse, s0, s1) < 8f;
        }
        if (!near || !MouseHeading(mouse, center, out var angle))
            return false;
        _ringDrag = true;
        _ringNode = g.Node;
        _ringProperty = g.Property;
        _ringStart = _ringCurrent = (float)g.Property.GetValue(g.Node)!;
        _ringLastAngle = angle;
        return true;
    }

    void ContinueRingDrag(float2 mouse)
    {
        if (!MouseHeading(mouse, RingCenter(_ringNode!), out var angle))
            return;
        float delta = angle - _ringLastAngle;
        delta -= MathF.Round(delta / 360) * 360;   // passage par ±180°
        _ringLastAngle = angle;
        _ringCurrent += delta;
        _ringProperty!.SetValue(_ringNode, _ringCurrent);
        _session.Touch();
    }

    void EndRingDrag()
    {
        _ringProperty!.SetValue(_ringNode, _ringStart);
        if (MathF.Abs(_ringCurrent - _ringStart) > 0.01f)
            _session.History.Execute(new SetPropertyCommand(_ringNode!, _ringProperty, MathF.Round(_ringCurrent)));
        _ringDrag = false;
        _ringNode = null;
        _ringProperty = null;
    }

    // ----- Entrées -----------------------------------------------------------------------------------

    void HandleInput(bool hovered)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos - _view.ImageMin;

        if (_dragAxis >= 0)
        {
            ContinueGizmoDrag(io.MouseDelta);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndGizmoDrag();
            return;
        }
        if (_ringDrag)
        {
            ContinueRingDrag(mouse);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndRingDrag();
            return;
        }

        if (!hovered)
            return;

        if (io.MouseWheel != 0)
            _view.Zoom(io.MouseWheel);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (TryStartGizmoDrag(mouse) || TryStartRingDrag(mouse)))
            return;

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f) && !io.KeyShift)
            _view.Orbit(io.MouseDelta);
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f) && io.KeyShift)
            _view.Pan(io.MouseDelta);

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !ImGui.Internal.IsMouseDragPastThreshold(ImGuiMouseButton.Left, 3f))
            Pick(mouse);

        if (ImGui.IsKeyPressed(ImGuiKey.F) && !io.WantTextInput)
            FrameSelection();
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            FrameSelection();
    }

    void Pick(float2 mouse)
    {
        var (world, bake) = Source();
        if (world is null || bake is null)
        {
            _session.Selection = null;
            return;
        }
        var hit = _view.Pick(world, mouse);
        _session.Selection = !hit.IsNull && bake.OwnerOf.TryGetValue(hit, out var owner) ? _session.FindNode(owner) : null;
        if (_session.Play is not null)
            _session.SelectedEntity = hit.IsNull ? -1 : hit.Index;
    }

    public void FrameSelection()
    {
        _framedOnce = true;
        var (world, bake) = Source();
        if (world is null)
            return;
        int sel = _session.SelectedEntity;
        if (_session.Play is not null && sel >= 0 && !world.EntityAt(sel).IsNull && world.Store<Transform>().Has(sel))
        {
            var p = world.Store<Transform>().Get(sel).Position;
            float r = world.Store<Renderable>().Has(sel) ? world.Store<Renderable>().Get(sel).BoundingRadius : 500;
            _view.FramePoints([p - new float3(r * 4), p + new float3(r * 4)]);
            return;
        }
        if (_session.Selection is null || bake is null)
        {
            _view.FrameAll(world);
            return;
        }
        var points = new List<float3>();
        var transforms = world.Store<Transform>();
        var renderables = world.Store<Renderable>();
        var selected = SelectedSubtree();
        foreach (var (entity, owner) in bake.OwnerOf)
            if (world.IsAlive(entity) && transforms.Has(entity.Index) && selected.Contains(owner))
            {
                // Le centre et l'étendue : un astre de 45 km se cadre en entier, pas depuis son intérieur.
                var p = transforms.Get(entity.Index).Position;
                float r = renderables.Has(entity.Index) ? renderables.Get(entity.Index).BoundingRadius : 0;
                points.Add(p - new float3(r));
                points.Add(p + new float3(r));
            }
        if (points.Count > 0)
            _view.FramePoints(points);
    }

    // ----- Surimpression -----------------------------------------------------------------------------

    void DrawOverlay()
    {
        var dl = ImGui.GetWindowDrawList();
        var (world, _) = Source();
        var pos = _view.ImageMin + new float2(10, 8);
        string mode = _session.PlayState switch
        {
            PlayState.Playing => "EN JEU",
            PlayState.Paused => "EN PAUSE",
            _ => "ÉDITION",
        };
        var modeColor = _session.PlayState == PlayState.Editing ? EditorTheme.Muted : EditorTheme.Accent;
        dl.AddText(pos, Widgets.Col(modeColor), mode);
        dl.AddText(pos + new float2(0, 18), Widgets.Col(EditorTheme.Muted),
            $"{world?.EntityCount ?? 0} entités · grille {WorldView.FormatDistance(GridStep)} · caméra à {WorldView.FormatDistance(_view.Distance)}");
        _view.DrawOverlay(dl, pos + new float2(0, 40));

        var bottom = _view.ImageMin + new float2(10, _view.ImageSize.Y - 26);
        if (_session.PreviewError is { } error && _session.Play is null)
            dl.AddText(bottom, Widgets.Col(EditorTheme.Error), $"Aperçu impossible : {error}");
        if (_session.Modules.Module is null)
            dl.AddText(bottom, Widgets.Col(EditorTheme.Warning), "Aucun module de jeu chargé : compilez le jeu (Ctrl+B)."u8);

        // Étiquettes des nœuds de la scène (flottes, astres) au-dessus de leur position, en édition.
        if (_session.Play is null)
            foreach (var node in _session.Scene.DescendantsAndSelf())
            {
                var p = node.GetType().GetProperty("Position");
                if (p?.PropertyType != typeof(float3) || !_view.Project((float3)p.GetValue(node)!, out var s))
                    continue;
                if (s.X < 0 || s.Y < 0 || s.X > _view.ImageSize.X || s.Y > _view.ImageSize.Y)
                    continue;
                var color = node == _session.Selection ? EditorTheme.Accent : new float4(0.85f, 0.85f, 0.9f, 0.85f);
                dl.AddText(_view.ImageMin + s + new float2(8, -24), Widgets.Col(color), node.Name);
            }
    }
}
