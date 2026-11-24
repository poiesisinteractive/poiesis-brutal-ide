using Brutal.ImGuiApi;
using Brutal.Numerics;
using Poiesis.Engine;
using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;
using Poiesis.Render;
using Poiesis.Render.Vulkan;

namespace Poiesis.Runtime;

/// <summary>
/// La vue 3D d'un monde, commune à l'éditeur et au joueur : caméra orbitale, entités dessinables
/// (<see cref="Renderable"/>) avec une taille apparente minimale, dessins du jeu (<see cref="IWorldVisualizer"/>),
/// HUD et étiquettes en surimpression, projections écran ↔ monde et sélection au clic.
/// </summary>
public sealed class WorldView
{
    /// <summary>Taille apparente minimale d'une entité (pixels) : de loin, la vue devient tactique.</summary>
    public const float MinApparentPixels = 22f;
    public const float Fov = 0.9f;

    readonly Dictionary<MeshKind, MeshHandle> _meshes = new();
    readonly SceneRenderer _renderer;

    /// <summary>Maillages du jeu téléversés, par objet (un maillage régénéré est un nouvel objet), avec leur dernière image d'usage.</summary>
    readonly Dictionary<MeshAsset, (MeshHandle Handle, long LastUsed)> _assets = new(ReferenceEqualityComparer.Instance);
    long _frameNumber;

    /// <summary>Un maillage du jeu inutilisé pendant ce nombre d'images est libéré (concepteur de coques, rechargement).</summary>
    public const int AssetIdleFrames = 120;

    public WorldView(SceneRenderer renderer)
    {
        _renderer = renderer;
        void Upload(MeshKind kind, MeshData mesh) =>
            _meshes[kind] = renderer.UploadMesh(mesh.Positions.ToArray(), mesh.Normals.ToArray(), mesh.Indices.ToArray());
        Upload(MeshKind.Box, Meshes.Box());
        Upload(MeshKind.Sphere, Meshes.Sphere());
        Upload(MeshKind.Hull, Meshes.Hull());
        Upload(MeshKind.HeavyHull, Meshes.HeavyHull());
    }

    // Caméra orbitale autour d'une cible.
    public float3 Target;
    public float Distance = 180_000;
    public float Yaw = 0.6f;
    public float Pitch = 0.55f;

    /// <summary>Rectangle de l'image à l'écran (pixels).</summary>
    public float2 ImageMin;
    public float2 ImageSize = new(1, 1);

    public float4x4 ViewProj { get; private set; }

    public RenderFrame Frame { get; } = new();

    /// <summary>Ce que le jeu a dessiné à cette image (traits, étiquettes, HUD).</summary>
    public DrawContext Draw { get; } = new();

    public float PixelsPerRadian => ImageSize.Y / Fov;

    /// <summary>L'auditeur du son : la caméra, son axe droit (panoramique) et sa distance au point visé.</summary>
    public Poiesis.Engine.Audio.Listener Listener()
    {
        var eye = EyePosition();
        var forward = float3.NormalizeOrZero(Target - eye);
        var right = float3.NormalizeOrZero(float3.Cross(forward, new float3(0, 1, 0)));
        return new Poiesis.Engine.Audio.Listener(eye, right, Distance);
    }

    public float3 EyePosition()
    {
        var dir = new float3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
        return Target + dir * Distance;
    }

    /// <summary>Commence une image : vide les listes, place la caméra.</summary>
    public void Begin()
    {
        _frameNumber++;
        ReleaseIdleAssets();
        Frame.Clear();
        Draw.Clear();
        Frame.Camera = new Camera
        {
            Position = EyePosition(),
            Target = Target,
            VerticalFovRadians = Fov,
            Near = MathF.Max(Distance * 0.002f, 1f),
            Far = Distance * 50f + 1_000_000f,
        };
        ViewProj = Frame.Camera.View * Frame.Camera.Projection(ImageSize.X / ImageSize.Y);
    }

    /// <summary>Les entités dessinables du monde ; <paramref name="highlight"/> éclaircit celles qu'il désigne (sélection).</summary>
    public void DrawEntities(World world, Func<Entity, bool>? highlight = null) => DrawEntities(world, highlight, null);

    /// <summary>
    /// Les entités dessinables du monde. Avec <paramref name="only"/>, seules celles pour lesquelles il rend une couleur
    /// sont dessinées, de cette couleur (fantômes d'une proposition, nœuds à supprimer).
    /// </summary>
    public void DrawEntities(World world, Func<Entity, bool>? highlight, Func<Entity, float4?>? only)
    {
        var eye = EyePosition();
        var transforms = world.Store<Transform>();
        var renderables = world.Store<Renderable>();
        world.TryResource<MeshLibrary>(out var library);
        var entities = renderables.DenseEntities;
        var items = renderables.Components;
        for (int i = 0; i < entities.Length; i++)
        {
            if (!transforms.Has(entities[i]))
                continue;
            ref readonly var r = ref items[i];
            var t = transforms.Get(entities[i]);
            var color = r.Color;
            if (only is not null)
            {
                if (only(world.EntityAt(entities[i])) is not { } forced)
                    continue;
                color = forced;
            }
            if (highlight is not null && highlight(world.EntityAt(entities[i])))
                color = new float4(MathF.Min(color.X * 1.3f + 0.15f, 1), MathF.Min(color.Y * 1.3f + 0.10f, 1), MathF.Min(color.Z * 0.6f + 0.35f, 1), 1);

            // De loin, une coque de 600 m ne ferait qu'un pixel : taille apparente minimale (vue tactique).
            var size = r.Size;
            float apparent = size.Z / MathF.Max(float3.Distance(eye, t.Position), 1f) * PixelsPerRadian;
            if (apparent < MinApparentPixels)
                size *= MinApparentPixels / MathF.Max(apparent, 0.01f);

            var m = float4x4.CreateScale(size) * float4x4.CreateFromQuaternion(t.Rotation) * float4x4.CreateTranslation(t.Position);
            var handle = r.Asset != 0 && library?.Get(r.Asset) is { } asset ? MeshOf(asset) : _meshes[r.Mesh];
            Frame.Draw(handle, new MeshInstance { World = m, Color = color });
        }
    }

    /// <summary>Le handle GPU d'un maillage du jeu, téléversé à la première utilisation.</summary>
    public MeshHandle MeshOf(MeshAsset asset)
    {
        if (_assets.TryGetValue(asset, out var entry))
        {
            _assets[asset] = (entry.Handle, _frameNumber);
            return entry.Handle;
        }
        var mesh = asset.Mesh;
        var handle = _renderer.UploadMesh(mesh.Positions.ToArray(), mesh.Normals.ToArray(), mesh.TexCoords.ToArray(), mesh.Indices.ToArray(),
            asset.Texture is { } tex ? (new int2(tex.Width, tex.Height), tex.Rgba) : null);
        _assets[asset] = (handle, _frameNumber);
        return handle;
    }

    /// <summary>Nombre de maillages du jeu présents sur le GPU pour cette vue.</summary>
    public int AssetCount => _assets.Count;

    void ReleaseIdleAssets()
    {
        List<MeshAsset>? idle = null;
        foreach (var (asset, entry) in _assets)
            if (_frameNumber - entry.LastUsed > AssetIdleFrames)
                (idle ??= new()).Add(asset);
        if (idle is null)
            return;
        foreach (var asset in idle)
        {
            _renderer.ReleaseMesh(_assets[asset].Handle);
            _assets.Remove(asset);
        }
    }

    /// <summary>Tirs, rayons, effets, aides de débogage et HUD dessinés par le jeu, plus le repère de l'entité sélectionnée.</summary>
    public void DrawGame(World world, IWorldVisualizer? visualizer, IEnumerable<string>? channels, int selectedEntity)
    {
        if (visualizer is null)
            return;
        Draw.EnabledChannels.Clear();
        if (channels is not null)
            Draw.EnabledChannels.UnionWith(channels);
        Draw.CameraPosition = EyePosition();
        Draw.PixelsPerRadian = PixelsPerRadian;
        Draw.SelectedEntity = selectedEntity;
        try
        {
            visualizer.Draw(world, Draw);
        }
        catch (Exception ex)
        {
            Draw.HudLine($"Erreur du dessin du jeu : {ex.Message}", EditorTheme.Error);
        }
        foreach (var l in Draw.Lines)
            Frame.Line(l.A, l.B, l.Color);
        foreach (var g in Draw.Glows)
            Frame.Glow(g.At, g.Radius, g.Color);

        if (selectedEntity >= 0 && !world.EntityAt(selectedEntity).IsNull && world.Store<Transform>().Has(selectedEntity))
        {
            var p = world.Store<Transform>().Get(selectedEntity).Position;
            float r = MathF.Max(world.Store<Renderable>().Has(selectedEntity) ? world.Store<Renderable>().Get(selectedEntity).BoundingRadius * 1.3f : 0,
                Draw.MetersForPixels(p, 18));
            Circle(p, r, EditorTheme.Accent);
        }
    }

    public void Circle(float3 center, float radius, float4 color, int segments = 48)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            Frame.Line(center + new float3(MathF.Cos(a0), 0, MathF.Sin(a0)) * radius, center + new float3(MathF.Cos(a1), 0, MathF.Sin(a1)) * radius, color);
        }
    }

    /// <summary>HUD du jeu (à partir de <paramref name="at"/>) et étiquettes ancrées dans le monde, en surimpression.</summary>
    public void DrawOverlay(ImDrawListPtr dl, float2 at)
    {
        foreach (var (text, color) in Draw.Hud)
        {
            dl.AddText(at, EditorTheme.Col(color), text);
            at.Y += 18;
        }
        foreach (var label in Draw.Labels)
            if (Project(label.At, out var s) && s.X >= 0 && s.Y >= 0 && s.X < ImageSize.X && s.Y < ImageSize.Y)
                dl.AddText(ImageMin + s + new float2(6, -8), EditorTheme.Col(label.Color), label.Text);
    }

    // ----- Caméra --------------------------------------------------------------------------------------

    public void Orbit(float2 mouseDelta)
    {
        Yaw -= mouseDelta.X * 0.006f;
        Pitch = Math.Clamp(Pitch + mouseDelta.Y * 0.006f, -1.5f, 1.5f);
    }

    public void Pan(float2 delta)
    {
        var forward = float3.Normalize(Target - EyePosition());
        var right = float3.Normalize(float3.Cross(forward, new float3(0, 1, 0)));
        var up = float3.Cross(right, forward);
        float worldPerPixel = 2 * Distance * MathF.Tan(Fov / 2) / ImageSize.Y;
        Target += (-right * delta.X + up * delta.Y) * worldPerPixel;
    }

    public void Zoom(float wheel) => Distance = Math.Clamp(Distance * MathF.Pow(0.85f, wheel), 20f, 5_000_000f);

    /// <summary>Cadre toutes les entités ; avec <paramref name="maxRadius"/>, ignore les plus grosses (astres de décor).</summary>
    public void FrameAll(World world, float maxRadius = float.MaxValue, float minRadius = 2000f)
    {
        var points = new List<float3>();
        var transforms = world.Store<Transform>();
        var renderables = world.Store<Renderable>();
        var dense = transforms.DenseEntities;
        for (int i = 0; i < dense.Length; i++)
        {
            float r = renderables.Has(dense[i]) ? renderables.Get(dense[i]).BoundingRadius : 0;
            if (r > maxRadius)
                continue;
            // Le centre et l'étendue : un vaisseau seul se cadre en entier.
            var p = transforms.Components[i].Position;
            points.Add(p - new float3(r));
            points.Add(p + new float3(r));
        }
        if (points.Count > 0)
            FramePoints(points, minRadius);
    }

    public void FramePoints(IReadOnlyList<float3> points, float minRadius = 2000f)
    {
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = float3.Min(min, p);
            max = float3.Max(max, p);
        }
        Target = (min + max) * 0.5f;
        float radius = MathF.Max(float3.Distance(min, max) * 0.5f, minRadius);
        Distance = radius / MathF.Tan(Fov / 2) * 1.1f;
    }

    // ----- Projections et sélection --------------------------------------------------------------------

    static float4 Transform(float3 p, in float4x4 m) => m.X * p.X + m.Y * p.Y + m.Z * p.Z + m.W;   // convention lignes : v * M

    /// <summary>Position à l'écran, relative à <see cref="ImageMin"/> ; faux si le point est derrière la caméra.</summary>
    public bool Project(float3 world, out float2 screen)
    {
        var clip = Transform(world, ViewProj);
        if (clip.W <= 1e-4f)
        {
            screen = default;
            return false;
        }
        var ndc = new float2(clip.X / clip.W, clip.Y / clip.W);
        screen = new float2((ndc.X * 0.5f + 0.5f) * ImageSize.X, (0.5f - ndc.Y * 0.5f) * ImageSize.Y);
        return true;
    }

    /// <summary>Rayon du monde sous un point de l'image (relatif à <see cref="ImageMin"/>).</summary>
    public bool Ray(float2 mouse, out float3 origin, out float3 dir)
    {
        origin = default;
        dir = default;
        if (!float4x4.Invert(ViewProj, out var inv))
            return false;
        var ndc = new float2(mouse.X / ImageSize.X * 2 - 1, 1 - mouse.Y / ImageSize.Y * 2);
        var near = Transform(new float3(ndc.X, ndc.Y, 0), inv);
        var far = Transform(new float3(ndc.X, ndc.Y, 1), inv);
        origin = new float3(near.X, near.Y, near.Z) / near.W;
        var f = new float3(far.X, far.Y, far.Z) / far.W;
        dir = float3.Normalize(f - origin);
        return true;
    }

    /// <summary>L'entité dessinable la plus proche sous la souris (avec la même taille apparente minimale qu'à l'affichage).</summary>
    public Entity Pick(World world, float2 mouse)
    {
        if (!Ray(mouse, out var origin, out var dir))
            return Entity.Null;
        float best = float.MaxValue;
        Entity hit = Entity.Null;
        var transforms = world.Store<Transform>();
        var renderables = world.Store<Renderable>();
        var entities = renderables.DenseEntities;
        var items = renderables.Components;
        for (int i = 0; i < entities.Length; i++)
        {
            if (!transforms.Has(entities[i]))
                continue;
            var center = transforms.Get(entities[i]).Position;
            float distance = MathF.Max(float3.Distance(origin, center), 1f);
            float radius = MathF.Max(items[i].BoundingRadius, MinApparentPixels * 0.6f * distance / PixelsPerRadian);
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
        return hit;
    }

    public static float DistanceToSegment(float2 p, float2 a, float2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(float2.Dot(p - a, ab) / MathF.Max(float2.Dot(ab, ab), 1e-6f), 0, 1);
        return float2.Distance(p, a + ab * t);
    }

    public static string FormatDistance(float meters) =>
        meters >= 1000 ? $"{meters / 1000:0.#} km" : $"{meters:0} m";
}

/// <summary>Les textures ImGui des cibles du viewport, réenregistrées quand les cibles sont recréées.</summary>
public sealed class ViewportTextures(Brutal.ImGuiApi.Backends.VulkanBackend backend, SceneRenderer renderer)
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
