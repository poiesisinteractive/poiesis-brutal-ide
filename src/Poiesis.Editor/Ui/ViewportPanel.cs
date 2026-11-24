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
/// La vue 3D : rendu hors écran affiché dans une fenêtre ImGui. En édition, on voit l'aperçu cuit de la scène ;
/// en jeu, le monde de la partie. Caméra orbitale, sélection au clic, gizmo de translation sur trois axes.
/// </summary>
sealed class ViewportPanel
{
    readonly EditorSession _session;
    readonly SceneRenderer _renderer;
    readonly VulkanBackendTextures _textures;
    readonly Dictionary<MeshKind, MeshHandle> _meshes = new();
    readonly RenderFrame _frame = new();

    // Caméra orbitale autour d'une cible.
    float3 _target;
    float _distance = 180_000;
    float _yaw = 0.6f;
    float _pitch = 0.55f;
    const float Fov = 0.9f;

    float2 _imageMin;
    float2 _imageSize = new(1, 1);
    float4x4 _viewProj;

    // Gizmo : axe saisi (-1 = aucun), nœud et valeur de départ.
    int _dragAxis = -1;
    Node? _dragNode;
    PropertyInfo? _dragProperty;
    float3 _dragStart;
    float3 _dragCurrent;
    bool _framedOnce;

    /// <summary>Taille apparente minimale d'une coque (pixels) : de loin, la vue devient tactique.</summary>
    const float MinApparentPixels = 22f;

    static readonly float3[] Axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    static readonly float4[] AxisColors = [new(0.95f, 0.30f, 0.30f, 1), new(0.40f, 0.90f, 0.35f, 1), new(0.35f, 0.55f, 1f, 1)];

    public ViewportPanel(EditorSession session, SceneRenderer renderer, VulkanBackendTextures textures)
    {
        _session = session;
        _renderer = renderer;
        _textures = textures;
        Upload(MeshKind.Box, Meshes.Box());
        Upload(MeshKind.Sphere, Meshes.Sphere());
        Upload(MeshKind.Hull, Meshes.Hull());
        Upload(MeshKind.HeavyHull, Meshes.HeavyHull());
    }

    void Upload(MeshKind kind, MeshData mesh) =>
        _meshes[kind] = _renderer.UploadMesh(mesh.Positions.ToArray(), mesh.Normals.ToArray(), mesh.Indices.ToArray());

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
        _imageMin = ImGui.GetCursorScreenPos();
        _imageSize = new float2(size.X, size.Y);

        if (!_framedOnce && Source().World is { } w && w.EntityCount > 0)
        {
            FrameAll(w);
            _framedOnce = true;
        }

        ImGui.Image(_textures.Viewport(slot), _imageSize);
        bool hovered = ImGui.IsItemHovered();
        HandleInput(hovered);
        DrawOverlay();
        ImGui.End();
    }

    /// <summary>Prépare l'image 3D de cette frame (à appeler après <see cref="Draw"/>, avant la passe de la fenêtre).</summary>
    public RenderFrame BuildFrame()
    {
        _frame.Clear();
        var eye = EyePosition();
        _frame.Camera = new Camera
        {
            Position = eye,
            Target = _target,
            VerticalFovRadians = Fov,
            Near = MathF.Max(_distance * 0.002f, 1f),
            Far = _distance * 50f + 1_000_000f,
        };
        _viewProj = _frame.Camera.View * _frame.Camera.Projection(_imageSize.X / _imageSize.Y);

        DrawGrid();

        var (world, bake) = Source();
        if (world is not null)
        {
            var selected = _session.Selection;
            var transforms = world.Store<Transform>();
            var renderables = world.Store<Renderable>();
            var entities = renderables.DenseEntities;
            var items = renderables.Components;
            float pixelsPerRadian = _imageSize.Y / Fov;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!transforms.Has(entities[i]))
                    continue;
                ref readonly var r = ref items[i];
                var t = transforms.Get(entities[i]);
                var color = r.Color;

                bool isSelected = selected is not null && bake is not null
                    && bake.OwnerOf.TryGetValue(world.EntityAt(entities[i]), out var owner)
                    && IsUnder(owner, selected);
                if (isSelected)
                    color = new float4(MathF.Min(color.X * 1.3f + 0.15f, 1), MathF.Min(color.Y * 1.3f + 0.10f, 1), MathF.Min(color.Z * 0.6f + 0.35f, 1), 1);

                // De loin, une coque de 600 m ne ferait qu'un pixel : on garde une taille apparente minimale (vue tactique).
                var sizeM = r.Size;
                float apparent = sizeM.Z / MathF.Max(float3.Distance(eye, t.Position), 1f) * pixelsPerRadian;
                if (apparent < MinApparentPixels)
                    sizeM *= MinApparentPixels / MathF.Max(apparent, 0.01f);

                var worldM = float4x4.CreateScale(sizeM) * float4x4.CreateFromQuaternion(t.Rotation) * float4x4.CreateTranslation(t.Position);
                _frame.Draw(_meshes[r.Mesh], new MeshInstance { World = worldM, Color = color });
            }
        }

        DrawGizmo();
        return _frame;
    }

    bool IsUnder(Guid ownerId, Node selected)
    {
        var owner = _session.FindNode(ownerId);
        return owner is not null && selected.IsSelfOrAncestorOf(owner);
    }

    float3 EyePosition()
    {
        var dir = new float3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
        return _target + dir * _distance;
    }

    void DrawGrid()
    {
        // Pas de grille adapté au zoom (puissance de 10), lignes majeures tous les 10 pas.
        float step = MathF.Pow(10, MathF.Floor(MathF.Log10(MathF.Max(_distance / 4f, 10f))));
        const int half = 20;
        float cx = MathF.Round(_target.X / step) * step, cz = MathF.Round(_target.Z / step) * step;
        var minor = new float4(0.30f, 0.32f, 0.42f, 0.35f);
        var major = new float4(0.45f, 0.48f, 0.62f, 0.70f);
        for (int i = -half; i <= half; i++)
        {
            float x = cx + i * step, z = cz + i * step;
            var c = MathF.Abs(MathF.IEEERemainder(x, step * 10)) < step * 0.5f ? major : minor;
            _frame.Line(new float3(x, 0, cz - half * step), new float3(x, 0, cz + half * step), c);
            c = MathF.Abs(MathF.IEEERemainder(z, step * 10)) < step * 0.5f ? major : minor;
            _frame.Line(new float3(cx - half * step, 0, z), new float3(cx + half * step, 0, z), c);
        }
        GridStep = step;
    }

    public float GridStep { get; private set; }

    // ----- Gizmo de translation ----------------------------------------------------------------------

    /// <summary>La position éditable du nœud sélectionné : sa propriété <c>Position</c> (float3), s'il en a une.</summary>
    (Node Node, PropertyInfo Property)? GizmoTarget()
    {
        if (_session.Selection is not { } node || _session.Play is not null)
            return null;
        var p = node.GetType().GetProperty("Position");
        return p is not null && p.PropertyType == typeof(float3) && p.CanWrite ? (node, p) : null;
    }

    float GizmoLength(float3 at) => float3.Distance(EyePosition(), at) * 0.15f;

    void DrawGizmo()
    {
        if (GizmoTarget() is not { } g)
            return;
        var pos = (float3)g.Property.GetValue(g.Node)!;
        var len = GizmoLength(pos);
        for (int a = 0; a < 3; a++)
        {
            var c = _dragAxis == a ? new float4(1, 1, 0.4f, 1) : AxisColors[a];
            _frame.Line(pos, pos + Axes[a] * len, c);
            // Petite pointe : trois segments vers l'extrémité.
            var tip = pos + Axes[a] * len;
            var side = Axes[(a + 1) % 3] * len * 0.06f;
            _frame.Line(tip, tip - Axes[a] * len * 0.15f + side, c);
            _frame.Line(tip, tip - Axes[a] * len * 0.15f - side, c);
        }
    }

    // ----- Entrées -----------------------------------------------------------------------------------

    void HandleInput(bool hovered)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos - _imageMin;

        if (_dragAxis >= 0)
        {
            ContinueGizmoDrag(io.MouseDelta);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndGizmoDrag();
            return;
        }

        if (!hovered)
            return;

        if (io.MouseWheel != 0)
            _distance = Math.Clamp(_distance * MathF.Pow(0.85f, io.MouseWheel), 20f, 5_000_000f);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && TryStartGizmoDrag(mouse))
            return;

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f) && !io.KeyShift)
        {
            _yaw -= io.MouseDelta.X * 0.006f;
            _pitch = Math.Clamp(_pitch + io.MouseDelta.Y * 0.006f, -1.5f, 1.5f);
        }
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f) && io.KeyShift)
            Pan(io.MouseDelta);

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !ImGui.Internal.IsMouseDragPastThreshold(ImGuiMouseButton.Left, 3f))
            Pick(mouse);

        if (ImGui.IsKeyPressed(ImGuiKey.F) && !io.WantTextInput)
            FrameSelection();
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            FrameSelection();
    }

    void Pan(float2 delta)
    {
        var eye = EyePosition();
        var forward = float3.Normalize(_target - eye);
        var right = float3.Normalize(float3.Cross(forward, new float3(0, 1, 0)));
        var up = float3.Cross(right, forward);
        float worldPerPixel = 2 * _distance * MathF.Tan(Fov / 2) / _imageSize.Y;
        _target += (-right * delta.X + up * delta.Y) * worldPerPixel;
    }

    bool TryStartGizmoDrag(float2 mouse)
    {
        if (GizmoTarget() is not { } g)
            return false;
        var pos = (float3)g.Property.GetValue(g.Node)!;
        var len = GizmoLength(pos);
        if (!Project(pos, out var s0))
            return false;
        int best = -1;
        float bestDist = 10f;   // pixels
        for (int a = 0; a < 3; a++)
        {
            if (!Project(pos + Axes[a] * len, out var s1))
                continue;
            var d = DistanceToSegment(mouse, s0, s1);
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
        if (!Project(_dragCurrent, out var s0) || !Project(_dragCurrent + axis * len, out var s1))
            return;
        var dir = s1 - s0;
        var lenSq = float2.Dot(dir, dir);
        if (lenSq < 1e-3f)
            return;
        var t = float2.Dot(mouseDelta, dir) / lenSq * len;
        _dragCurrent += axis * t;
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

    void Pick(float2 mouse)
    {
        var (world, bake) = Source();
        if (world is null || bake is null || !Ray(mouse, out var origin, out var dir))
        {
            _session.Selection = null;
            return;
        }

        float best = float.MaxValue;
        Entity hit = Entity.Null;
        var transforms = world.Store<Transform>();
        var renderables = world.Store<Renderable>();
        var entities = renderables.DenseEntities;
        var items = renderables.Components;
        float pixelsPerRadian = _imageSize.Y / Fov;
        for (int i = 0; i < entities.Length; i++)
        {
            if (!transforms.Has(entities[i]))
                continue;
            var center = transforms.Get(entities[i]).Position;
            float radius = items[i].BoundingRadius;
            // Même taille apparente minimale qu'à l'affichage, plus une marge pour viser facilement.
            float distance = MathF.Max(float3.Distance(origin, center), 1f);
            radius = MathF.Max(radius, MinApparentPixels * 0.6f * distance / pixelsPerRadian);
            var oc = center - origin;
            float along = float3.Dot(oc, dir);
            if (along < 0)
                continue;
            float perpSq = float3.Dot(oc, oc) - along * along;
            if (perpSq <= radius * radius && along < best)
            {
                best = along;
                hit = world.EntityAt(entities[i]);
            }
        }

        _session.Selection = !hit.IsNull && bake.OwnerOf.TryGetValue(hit, out var owner) ? _session.FindNode(owner) : null;
    }

    public void FrameSelection()
    {
        var (world, bake) = Source();
        if (_session.Selection is null || world is null || bake is null)
        {
            if (world is not null)
                FrameAll(world);
            return;
        }
        var points = new List<float3>();
        var transforms = world.Store<Transform>();
        foreach (var (entity, owner) in bake.OwnerOf)
            if (world.IsAlive(entity) && transforms.Has(entity.Index) && IsUnder(owner, _session.Selection))
                points.Add(transforms.Get(entity.Index).Position);
        if (points.Count > 0)
            Frame(points);
    }

    void FrameAll(World world)
    {
        var transforms = world.Store<Transform>();
        var points = new List<float3>();
        foreach (var t in transforms.Components)
            points.Add(t.Position);
        if (points.Count > 0)
            Frame(points);
    }

    void Frame(List<float3> points)
    {
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = float3.Min(min, p);
            max = float3.Max(max, p);
        }
        _target = (min + max) * 0.5f;
        float radius = MathF.Max(float3.Distance(min, max) * 0.5f, 2000f);
        _distance = radius / MathF.Tan(Fov / 2) * 1.1f;
    }

    // ----- Projections -------------------------------------------------------------------------------

    static float4 Transform(float3 p, in float4x4 m) =>
        m.X * p.X + m.Y * p.Y + m.Z * p.Z + m.W;   // convention lignes : v * M

    bool Project(float3 world, out float2 screen)
    {
        var clip = Transform(world, _viewProj);
        if (clip.W <= 1e-4f)
        {
            screen = default;
            return false;
        }
        var ndc = new float2(clip.X / clip.W, clip.Y / clip.W);
        screen = new float2((ndc.X * 0.5f + 0.5f) * _imageSize.X, (0.5f - ndc.Y * 0.5f) * _imageSize.Y);
        return true;
    }

    bool Ray(float2 mouse, out float3 origin, out float3 dir)
    {
        origin = default;
        dir = default;
        if (!float4x4.Invert(_viewProj, out var inv))
            return false;
        var ndc = new float2(mouse.X / _imageSize.X * 2 - 1, 1 - mouse.Y / _imageSize.Y * 2);
        var near = Transform(new float3(ndc.X, ndc.Y, 0), inv);
        var far = Transform(new float3(ndc.X, ndc.Y, 1), inv);
        origin = new float3(near.X, near.Y, near.Z) / near.W;
        var f = new float3(far.X, far.Y, far.Z) / far.W;
        dir = float3.Normalize(f - origin);
        return true;
    }

    static float DistanceToSegment(float2 p, float2 a, float2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(float2.Dot(p - a, ab) / MathF.Max(float2.Dot(ab, ab), 1e-6f), 0, 1);
        return float2.Distance(p, a + ab * t);
    }

    // ----- Surimpression -----------------------------------------------------------------------------

    void DrawOverlay()
    {
        var dl = ImGui.GetWindowDrawList();
        var (world, _) = Source();
        var pos = _imageMin + new float2(10, 8);
        string mode = _session.PlayState switch
        {
            PlayState.Playing => "EN JEU",
            PlayState.Paused => "EN PAUSE",
            _ => "ÉDITION",
        };
        var modeColor = _session.PlayState == PlayState.Editing ? EditorTheme.Muted : EditorTheme.Accent;
        dl.AddText(pos, Widgets.Col(modeColor), mode);
        dl.AddText(pos + new float2(0, 18), Widgets.Col(EditorTheme.Muted),
            $"{world?.EntityCount ?? 0} entités · grille {FormatDistance(GridStep)} · caméra à {FormatDistance(_distance)}");
        if (_session.PreviewError is { } error && _session.Play is null)
            dl.AddText(pos + new float2(0, 36), Widgets.Col(EditorTheme.Error), $"Aperçu impossible : {error}");
        if (_session.Modules.Module is null)
            dl.AddText(pos + new float2(0, 36), Widgets.Col(EditorTheme.Warning), "Aucun module de jeu chargé : compilez le jeu (Ctrl+B)."u8);

        // Étiquettes des nœuds racines de la scène (flottes…), au-dessus de leur position.
        if (_session.Play is null)
            foreach (var node in _session.Scene.DescendantsAndSelf())
            {
                var p = node.GetType().GetProperty("Position");
                if (p?.PropertyType != typeof(float3) || !Project((float3)p.GetValue(node)!, out var s))
                    continue;
                if (s.X < 0 || s.Y < 0 || s.X > _imageSize.X || s.Y > _imageSize.Y)
                    continue;
                var color = node == _session.Selection ? EditorTheme.Accent : new float4(0.85f, 0.85f, 0.9f, 0.85f);
                dl.AddText(_imageMin + s + new float2(8, -24), Widgets.Col(color), node.Name);
            }
    }

    public static string FormatDistance(float meters) =>
        meters >= 1000 ? $"{meters / 1000:0.#} km" : $"{meters:0} m";
}

/// <summary>Les textures ImGui des cibles du viewport, réenregistrées quand les cibles sont recréées.</summary>
sealed class VulkanBackendTextures(Brutal.ImGuiApi.Backends.VulkanBackend backend, SceneRenderer renderer)
{
    ImTextureRef[] _refs = [];
    int _version = -1;

    public ImTextureRef Viewport(int slot)
    {
        if (_version != renderer.TargetVersion)
        {
            foreach (var r in _refs)
                backend.RemoveTexture(r);
            _refs = Enumerable.Range(0, Swapchain.FramesInFlight)
                .Select(i => backend.AddTexture(renderer.Sampler, renderer.ColorView(i), Brutal.VulkanApi.VkImageLayout.ShaderReadOnlyOptimal))
                .ToArray();
            _version = renderer.TargetVersion;
        }
        return _refs[slot];
    }
}
