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

        if (_session.Play is null)
            Toolbar();
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
        if (_session.Play is null)
            DrawPlan(world, bake);
        DrawGizmo();
        if (Extensions is not null)
            foreach (var l in Extensions.GizmoDraw.Lines)
                _view.Frame.Line(l.A, l.B, l.Color);
        if (world is not null)
            _view.DrawGame(world, _session.Modules.Module as IWorldVisualizer, DebugChannels, _session.Play is not null ? _session.SelectedEntity : -1);
        return _view.Frame;
    }

    static readonly float4 GhostColor = new(0.35f, 0.9f, 1f, 0.5f);
    static readonly float4 RemovedColor = new(1f, 0.3f, 0.3f, 0.75f);

    /// <summary>
    /// Ce que l'IA propose : les nœuds ajoutés ou modifiés en fantôme (cyan, à leur nouvelle place), les nœuds à
    /// supprimer en rouge, et un trait de l'ancienne position à la nouvelle.
    /// </summary>
    void DrawPlan(World? world, BakeContext? bake)
    {
        if (_session.Plan is not { } plan || _session.PlanWorld is not { } planWorld || _session.PlanBake is not { } planBake)
            return;
        var chosen = plan.Ops.Where(_session.PlanChosen.Contains).ToList();
        var affected = plan.AffectedIds(chosen);
        var removed = plan.RemovedIds(chosen);
        _view.DrawEntities(planWorld, null, e => planBake.OwnerOf.TryGetValue(e, out var owner) && affected.Contains(owner) ? GhostColor : null);
        if (world is not null && bake is not null && removed.Count > 0)
        {
            var removedSubtree = new HashSet<Guid>();
            foreach (var id in removed)
                if (_session.FindNode(id) is { } node)
                    foreach (var n in node.DescendantsAndSelf())
                        removedSubtree.Add(n.Id);
            _view.DrawEntities(world, null, e => bake.OwnerOf.TryGetValue(e, out var owner) && removedSubtree.Contains(owner) ? RemovedColor : null);
        }
        foreach (var op in chosen.OfType<SetPropertyOp>().Where(o => o.Property == "Position"))
            if (op.OldValue is not null && op.Value is not null)
            {
                var from = (float3)ValueCodec.FromJson(op.OldValue, typeof(float3))!;
                var to = (float3)ValueCodec.FromJson(op.Value, typeof(float3))!;
                _view.Frame.Line(from, to, GhostColor);
            }
    }

    readonly HashSet<Guid> _selectedIds = new();

    /// <summary>Le nœud sélectionné et ses descendants, calculé une fois par image (pas une fois par entité).</summary>
    HashSet<Guid> SelectedSubtree()
    {
        _selectedIds.Clear();
        foreach (var node in _session.SelectedNodes)
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

    // ----- Gizmos : déplacer, tourner, échelle --------------------------------------------------------

    public enum GizmoMode { Move, Rotate, Scale }

    /// <summary>Le gizmo affiché (W, E, R).</summary>
    public GizmoMode Mode { get; set; } = GizmoMode.Move;

    /// <summary>Axes dans le repère du nœud principal (son cap) plutôt que ceux du monde (L).</summary>
    public bool LocalFrame { get; set; }

    /// <summary>Magnétisme (M) : déplacements sur le pas de la grille, rotations par 15°, échelles par 10 % ; Ctrl l'inverse.</summary>
    public bool Snap { get; set; }

    bool SnapNow => Snap ^ ImGui.GetIO().KeyCtrl;

    TransformDrag? _drag;
    string _dragLabel = "";
    int _dragAxis = -1;          // déplacement : axe saisi
    float _dragAmount;           // déplacement (m) ou échelle (m le long de l'axe)
    float _dragPivotAlong;       // coordonnée du centre le long de l'axe au départ (magnétisme absolu)
    bool _ringDrag;
    float _ringAccum, _ringLastAngle;
    bool _scaleDrag;
    float _scaleLength;
    float _shownFactor = 1;

    static readonly float4[] AxisColors = [new(0.95f, 0.30f, 0.30f, 1), new(0.40f, 0.90f, 0.35f, 1), new(0.35f, 0.55f, 1f, 1)];
    static readonly float4 Active = new(1, 1, 0.4f, 1);

    /// <summary>Les nœuds sélectionnés qu'un gizmo peut transformer (ceux qui ont une position), hors partie.</summary>
    IReadOnlyList<Node> Movables() =>
        _session.Play is not null ? [] : SceneTransforms.Movable(_session.SelectedNodes);

    float3 Pivot(IReadOnlyList<Node> movables) => _drag?.Pivot is { } p && _ringDrag ? p : SceneTransforms.Pivot(movables);

    /// <summary>Les trois axes du gizmo : ceux du monde, ou ceux du nœud principal tourné de son cap.</summary>
    float3[] AxesFor()
    {
        if (LocalFrame && _session.Selection is { } primary && SceneTransforms.HeadingProperty(primary.GetType())?.GetValue(primary) is float h)
        {
            float yaw = h * MathF.PI / 180;
            return [new float3(MathF.Cos(yaw), 0, -MathF.Sin(yaw)), new float3(0, 1, 0), new float3(MathF.Sin(yaw), 0, MathF.Cos(yaw))];
        }
        return [new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1)];
    }

    float GizmoLength(float3 at) => float3.Distance(_view.EyePosition(), at) * 0.15f;

    bool CanRotate(IReadOnlyList<Node> movables) =>
        movables.Count > 1 || movables.Any(n => SceneTransforms.HeadingProperty(n.GetType()) is not null);

    bool CanScale(IReadOnlyList<Node> movables) =>
        movables.Count > 1 || movables.Any(n => SceneTransforms.ScaleProperty(n.GetType()) is not null);

    void DrawGizmo()
    {
        var movables = Movables();
        if (movables.Count == 0)
            return;
        var pos = Pivot(movables);
        var len = GizmoLength(pos);
        var axes = AxesFor();
        if (Mode == GizmoMode.Move)
            for (int a = 0; a < 3; a++)
            {
                var c = _dragAxis == a ? Active : AxisColors[a];
                var tip = pos + axes[a] * len;
                _view.Frame.Line(pos, tip, c);
                var side = axes[(a + 1) % 3] * len * 0.06f;
                _view.Frame.Line(tip, tip - axes[a] * len * 0.15f + side, c);
                _view.Frame.Line(tip, tip - axes[a] * len * 0.15f - side, c);
            }
        if (Mode is GizmoMode.Move or GizmoMode.Rotate && CanRotate(movables))
            DrawRing(pos, len * 0.75f, movables);
        if (Mode == GizmoMode.Scale && CanScale(movables))
        {
            var c = _scaleDrag ? Active : new float4(0.95f, 0.6f, 0.95f, 1);
            float shown = _scaleDrag ? _shownFactor : 1;
            var tip = pos + axes[0] * len * shown;
            _view.Frame.Line(pos, tip, c);
            float b = len * 0.06f;
            var u = axes[1] * b;
            var w = axes[2] * b;
            // Un carré au bout : la poignée d'échelle.
            _view.Frame.Line(tip - u - w, tip + u - w, c);
            _view.Frame.Line(tip + u - w, tip + u + w, c);
            _view.Frame.Line(tip + u + w, tip - u + w, c);
            _view.Frame.Line(tip - u + w, tip - u - w, c);
        }
    }

    void DrawRing(float3 center, float r, IReadOnlyList<Node> movables)
    {
        var color = _ringDrag ? Active : new float4(1f, 0.8f, 0.3f, 0.9f);
        const int n = 64;
        for (int i = 0; i < n; i++)
        {
            float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
            _view.Frame.Line(center + new float3(MathF.Sin(a0), 0, MathF.Cos(a0)) * r, center + new float3(MathF.Sin(a1), 0, MathF.Cos(a1)) * r, color);
        }
        // Le cap du nœud principal, s'il en a un.
        if (_session.Selection is { } primary && SceneTransforms.HeadingProperty(primary.GetType())?.GetValue(primary) is float h)
        {
            float yaw = h * MathF.PI / 180;
            _view.Frame.Line(center, center + new float3(MathF.Sin(yaw), 0, MathF.Cos(yaw)) * r, color);
        }
    }

    bool TryStartGizmo(float2 mouse)
    {
        var movables = Movables();
        if (movables.Count == 0)
            return false;
        var pos = SceneTransforms.Pivot(movables);
        var len = GizmoLength(pos);
        var axes = AxesFor();
        if (!_view.Project(pos, out var s0))
            return false;

        if (Mode == GizmoMode.Move)
        {
            int best = -1;
            float bestDist = 10f;   // pixels
            for (int a = 0; a < 3; a++)
                if (_view.Project(pos + axes[a] * len, out var s1) && WorldView.DistanceToSegment(mouse, s0, s1) is var d && d < bestDist)
                    (best, bestDist) = (a, d);
            if (best >= 0)
            {
                _drag = new TransformDrag(_session.SelectedNodes);
                _dragLabel = movables.Count == 1 ? $"Déplacer {movables[0].Name}" : $"Déplacer {movables.Count} nœuds";
                _dragAxis = best;
                _dragAmount = 0;
                _dragPivotAlong = float3.Dot(pos, axes[best]);
                return true;
            }
        }
        if (Mode is GizmoMode.Move or GizmoMode.Rotate && CanRotate(movables) && NearRing(mouse, pos, len * 0.75f) && MouseHeading(mouse, pos, out var angle))
        {
            _drag = new TransformDrag(_session.SelectedNodes);
            _dragLabel = movables.Count == 1 ? $"Tourner {movables[0].Name}" : $"Tourner {movables.Count} nœuds";
            _ringDrag = true;
            _ringAccum = 0;
            _ringLastAngle = angle;
            return true;
        }
        if (Mode == GizmoMode.Scale && CanScale(movables) && _view.Project(pos + axes[0] * len, out var tip) && float2.Distance(mouse, tip) < 14f)
        {
            _drag = new TransformDrag(_session.SelectedNodes);
            _dragLabel = movables.Count == 1 ? $"Mettre {movables[0].Name} à l'échelle" : $"Mettre {movables.Count} nœuds à l'échelle";
            _scaleDrag = true;
            _scaleLength = len;
            _dragAmount = 0;
            _shownFactor = 1;
            return true;
        }
        return false;
    }

    bool NearRing(float2 mouse, float3 center, float r)
    {
        const int n = 64;
        for (int i = 0; i < n; i++)
        {
            float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
            if (_view.Project(center + new float3(MathF.Sin(a0), 0, MathF.Cos(a0)) * r, out var s0)
                && _view.Project(center + new float3(MathF.Sin(a1), 0, MathF.Cos(a1)) * r, out var s1)
                && WorldView.DistanceToSegment(mouse, s0, s1) < 8f)
                return true;
        }
        return false;
    }

    /// <summary>Angle (degrés, convention du cap) du point visé par la souris dans le plan horizontal du centre.</summary>
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

    /// <summary>Déplacement de la souris projeté sur un axe à l'écran, en mètres.</summary>
    float AlongAxis(float2 mouseDelta, float3 at, float3 axis)
    {
        var len = GizmoLength(at);
        if (!_view.Project(at, out var s0) || !_view.Project(at + axis * len, out var s1))
            return 0;
        var dir = s1 - s0;
        var lenSq = float2.Dot(dir, dir);
        return lenSq < 1e-3f ? 0 : float2.Dot(mouseDelta, dir) / lenSq * len;
    }

    void ContinueGizmo(float2 mouse, float2 mouseDelta)
    {
        var axes = AxesFor();
        if (_dragAxis >= 0)
        {
            var axis = axes[_dragAxis];
            _dragAmount += AlongAxis(mouseDelta, _drag!.Pivot + axis * _dragAmount, axis);
            float amount = _dragAmount;
            if (SnapNow && GridStep > 0)
                amount = LocalFrame
                    ? MathF.Round(amount / GridStep) * GridStep
                    : MathF.Round((_dragPivotAlong + amount) / GridStep) * GridStep - _dragPivotAlong;   // le centre tombe sur la grille
            _drag.Translate(axis * amount);
        }
        else if (_ringDrag)
        {
            if (!MouseHeading(mouse, _drag!.Pivot, out var angle))
                return;
            float delta = angle - _ringLastAngle;
            delta -= MathF.Round(delta / 360) * 360;   // passage par ±180°
            _ringLastAngle = angle;
            _ringAccum += delta;
            _drag.Rotate(SnapNow ? MathF.Round(_ringAccum / 15) * 15 : _ringAccum);
        }
        else if (_scaleDrag)
        {
            _dragAmount += AlongAxis(mouseDelta, _drag!.Pivot + axes[0] * (_scaleLength + _dragAmount), axes[0]);
            float factor = Math.Clamp((_scaleLength + _dragAmount) / _scaleLength, 0.05f, 20f);
            if (SnapNow)
                factor = MathF.Max(0.1f, MathF.Round(factor * 10) / 10);
            _shownFactor = factor;
            _drag.Scale(factor);
        }
        _session.Touch();
    }

    void EndGizmo()
    {
        // Les valeurs finales deviennent une seule commande annulable, pour tout le groupe.
        if (_drag?.Commit(_dragLabel) is { } command)
            _session.History.Execute(command);
        _drag = null;
        _dragAxis = -1;
        _ringDrag = false;
        _scaleDrag = false;
        _shownFactor = 1;
    }

    bool Dragging => _drag is not null;

    // ----- Entrées -----------------------------------------------------------------------------------

    bool _rectSelecting;
    float2 _rectStart;

    void HandleInput(bool hovered)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos - _view.ImageMin;

        if (Dragging)
        {
            ContinueGizmo(mouse, io.MouseDelta);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndGizmo();
            return;
        }
        if (_rectSelecting)
        {
            var dl = ImGui.GetWindowDrawList();
            var a = _view.ImageMin + _rectStart;
            var b = io.MousePos;
            dl.AddRectFilled(float2.Min(a, b), float2.Max(a, b), Widgets.Col(EditorTheme.Accent * new float4(1, 1, 1, 0.15f)));
            dl.AddRect(float2.Min(a, b), float2.Max(a, b), Widgets.Col(EditorTheme.Accent));
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _rectSelecting = false;
                SelectInRect(_rectStart, mouse, additive: io.KeyCtrl);
            }
            return;
        }

        if (!hovered)
            return;

        if (io.MouseWheel != 0)
            _view.Zoom(io.MouseWheel);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (TryStartGizmo(mouse))
                return;
            if (io.KeyShift && _session.Play is null)
            {
                // Maj + glisser : sélection au rectangle.
                _rectSelecting = true;
                _rectStart = mouse;
                return;
            }
        }

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f) && !io.KeyShift || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f) && !io.KeyShift)
            _view.Orbit(io.MouseDelta);
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f) && io.KeyShift)
            _view.Pan(io.MouseDelta);

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !ImGui.Internal.IsMouseDragPastThreshold(ImGuiMouseButton.Left, 3f))
            Pick(mouse, toggle: io.KeyCtrl);

        if (!io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F))
                FrameSelection();
            if (!io.KeyCtrl)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.W, false))
                    Mode = GizmoMode.Move;
                if (ImGui.IsKeyPressed(ImGuiKey.E, false))
                    Mode = GizmoMode.Rotate;
                if (ImGui.IsKeyPressed(ImGuiKey.R, false))
                    Mode = GizmoMode.Scale;
                if (ImGui.IsKeyPressed(ImGuiKey.L, false))
                    LocalFrame = !LocalFrame;
                if (ImGui.IsKeyPressed(ImGuiKey.M, false))
                    Snap = !Snap;
            }
            CameraKeys(io.KeyCtrl);
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            FrameSelection();
    }

    void Pick(float2 mouse, bool toggle)
    {
        var (world, bake) = Source();
        if (world is null || bake is null)
        {
            if (!toggle)
                _session.Selection = null;
            return;
        }
        var hit = _view.Pick(world, mouse);
        var node = !hit.IsNull && bake.OwnerOf.TryGetValue(hit, out var owner) ? _session.FindNode(owner) : null;
        if (toggle)
        {
            if (node is not null)
                _session.ToggleSelection(node);
        }
        else
        {
            _session.Selection = node;
        }
        if (_session.Play is not null)
            _session.SelectedEntity = hit.IsNull ? -1 : hit.Index;
    }

    /// <summary>Sélectionne les nœuds dont une entité est dans le rectangle (en pixels de l'image).</summary>
    void SelectInRect(float2 a, float2 b, bool additive)
    {
        var (world, bake) = Source();
        if (world is null || bake is null)
            return;
        var min = float2.Min(a, b);
        var max = float2.Max(a, b);
        if (max.X - min.X < 3 && max.Y - min.Y < 3)
            return;
        var transforms = world.Store<Transform>();
        var found = new List<Node>(additive ? _session.SelectedNodes : []);
        foreach (var (entity, owner) in bake.OwnerOf)
        {
            if (!world.IsAlive(entity) || !transforms.Has(entity.Index) || !_view.Project(transforms.Get(entity.Index).Position, out var s))
                continue;
            if (s.X < min.X || s.Y < min.Y || s.X > max.X || s.Y > max.Y)
                continue;
            if (_session.FindNode(owner) is { } node && !found.Contains(node))
                found.Add(node);
        }
        _session.SetSelection(found);
    }

    // ----- Caméras enregistrées ----------------------------------------------------------------------

    /// <summary>Les caméras enregistrées (fournies par l'éditeur) : Ctrl+1 à 9 pour enregistrer, 1 à 9 pour revenir.</summary>
    public CameraBookmarks? Cameras { get; set; }

    void CameraKeys(bool ctrl)
    {
        if (Cameras is null)
            return;
        for (int i = 1; i <= 9; i++)
        {
            if (!ImGui.IsKeyPressed(ImGuiKey._1 + (i - 1), false))
                continue;
            if (ctrl)
                SaveCamera(i);
            else
                RecallCamera(i);
        }
    }

    public void SaveCamera(int slot) =>
        Cameras?.Save(_session.ScenePath, slot, new CameraBookmark { Target = _view.Target, Distance = _view.Distance, Yaw = _view.Yaw, Pitch = _view.Pitch });

    public bool RecallCamera(int slot)
    {
        if (Cameras?.Get(_session.ScenePath, slot) is not { } c)
            return false;
        _view.Target = c.Target;
        _view.Distance = c.Distance;
        _view.Yaw = c.Yaw;
        _view.Pitch = c.Pitch;
        _framedOnce = true;
        return true;
    }

    // ----- Barre d'outils ----------------------------------------------------------------------------

    void Toolbar()
    {
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new float2(6, 3));
        void ModeButton(string label, GizmoMode mode, string tip)
        {
            bool on = Mode == mode;
            if (on)
                ImGui.PushStyleColor(ImGuiCol.Button, EditorTheme.Accent * new float4(1, 1, 1, 0.6f));
            if (ImGui.SmallButton(label))
                Mode = mode;
            if (on)
                ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetItemTooltip(tip);
            ImGui.SameLine();
        }
        ModeButton("Déplacer", GizmoMode.Move, "Flèches : déplacer ; anneau : tourner (W)");
        ModeButton("Tourner", GizmoMode.Rotate, "Anneau : tourner le cap, ou le groupe autour de son centre (E)");
        ModeButton("Échelle", GizmoMode.Scale, "Carré : taille (rayon, espacement), et écart au centre pour un groupe (R)");
        ImGui.TextDisabled("|"u8);
        ImGui.SameLine();
        if (ImGui.SmallButton(LocalFrame ? "Local" : "Global"))
            LocalFrame = !LocalFrame;
        if (ImGui.IsItemHovered())
            ImGui.SetItemTooltip("Axes du monde, ou du nœud principal tourné de son cap (L)"u8);
        ImGui.SameLine();
        bool snap = Snap;
        if (ImGui.Checkbox("Magnétisme"u8, ref snap))
            Snap = snap;
        if (ImGui.IsItemHovered())
            ImGui.SetItemTooltip($"Pas de la grille ({WorldView.FormatDistance(GridStep)}), 15°, 10 % (M ; Ctrl l'inverse pendant le glissement)");
        int count = _session.SelectedNodes.Count;
        if (count > 1)
        {
            ImGui.SameLine();
            ImGui.TextColored(EditorTheme.Accent, $"{count} nœuds sélectionnés");
        }
    }

    public Poiesis.Engine.Audio.Listener Listener() => _view.Listener();

    /// <summary>Rapproche (facteur &lt; 1) ou éloigne la caméra (scripts).</summary>
    public void ZoomBy(float factor) => _view.Distance = Math.Clamp(_view.Distance * factor, 20f, 5_000_000f);

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
        if (_session.SelectedNodes.Count == 0 || bake is null)
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

        // Le HUD du jeu, en partie : le même que dans le jeu exporté.
        if (_session.Play is { } play && _session.Modules.Module is IGameHud hud)
        {
            var ctx = new HudContext
            {
                Min = _view.ImageMin,
                Size = _view.ImageSize,
                Project = p => _view.Project(p, out var s) ? _view.ImageMin + s : null,
                SelectedEntity = _session.SelectedEntity,
                Time = play.Time,
                TimeScale = (float)play.Clock.TimeScale,
                InEditor = true,
            };
            try
            {
                hud.DrawHud(play.World, ctx);
                _session.SelectedEntity = ctx.SelectedEntity;
            }
            catch (Exception ex)
            {
                dl.AddText(_view.ImageMin + new float2(10, _view.ImageSize.Y - 48), Widgets.Col(EditorTheme.Error), $"HUD du jeu : {ex.Message}");
            }
        }

        var bottom = _view.ImageMin + new float2(10, _view.ImageSize.Y - 26);
        if (_session.Plan is { } plan && _session.Play is null)
            dl.AddText(bottom - new float2(0, 22), Widgets.Col(new float4(0.35f, 0.9f, 1f, 1)),
                $"Aperçu de l'IA : {_session.PlanChosen.Count} modification(s) sur {plan.Ops.Count} cochée(s). Appliquer ou refuser dans le panneau IA, onglet Scène.");
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
                var color = _session.IsSelected(node) ? EditorTheme.Accent : new float4(0.85f, 0.85f, 0.9f, 0.85f);
                dl.AddText(_view.ImageMin + s + new float2(8, -24), Widgets.Col(color), node.Name);
            }
    }
}
