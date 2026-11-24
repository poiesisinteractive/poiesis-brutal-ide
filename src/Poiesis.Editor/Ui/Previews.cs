using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Poiesis.Engine;
using Poiesis.Engine.Assets;
using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;
using Poiesis.Render;
using Poiesis.Render.Vulkan;

namespace Poiesis.Editor.Ui;

/// <summary>
/// L'aperçu 3D que les extensions dessinent dans leurs panneaux (<c>IEditorContext.View3D</c>) : un rendu hors écran à
/// part, une caméra orbitale, un seul aperçu par image. Le monde n'est gardé que le temps de l'image.
/// </summary>
sealed class PreviewView : IDisposable
{
    readonly SceneRenderer _renderer;
    readonly WorldView _view;
    readonly ViewportTextures _textures;
    World? _world;
    DrawContext? _overlay;
    string? _lastId;
    int _usedFrame = -1;

    public PreviewView(VulkanContext vk, VulkanBackend backend)
    {
        _renderer = new SceneRenderer(vk, Swapchain.FramesInFlight);
        _view = new WorldView(_renderer);
        _textures = new ViewportTextures(backend, _renderer);
    }

    /// <summary>Le slot de l'image en cours (fixé par l'éditeur à chaque image).</summary>
    public int Slot { get; set; }

    public int Frame { get; set; }

    public bool Draw(string id, World world, float2 size, DrawContext? overlay)
    {
        if (_usedFrame == Frame)
        {
            ImGui.TextDisabled("(un seul aperçu 3D par image)"u8);
            return false;
        }
        _usedFrame = Frame;
        var pixels = new int2(Math.Max((int)size.X, 16), Math.Max((int)size.Y, 16));
        _renderer.Resize(pixels);
        _view.ImageMin = ImGui.GetCursorScreenPos();
        _view.ImageSize = new float2(pixels.X, pixels.Y);
        if (_lastId != id)
        {
            _lastId = id;
            _view.Yaw = 2.3f;
            _view.Pitch = 0.45f;
            FrameSubject(world);
        }
        ImGui.Image(_textures.Viewport(Slot), _view.ImageSize);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
            _view.Orbit(io.MouseDelta);
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 1f))
            _view.Pan(io.MouseDelta);
        if (hovered && io.MouseWheel != 0)
            _view.Zoom(io.MouseWheel);
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            FrameSubject(world);

        // Étiquettes de l'aperçu, en surimpression.
        if (overlay is not null && _view.ImageSize.X > 0)
        {
            _view.Begin();   // pour les projections ; le rendu recommencera l'image proprement
            var dl = ImGui.GetWindowDrawList();
            foreach (var label in overlay.Labels)
                if (_view.Project(label.At, out var s))
                    dl.AddText(_view.ImageMin + s + new float2(6, -8), EditorTheme.Col(label.Color), label.Text);
        }
        _world = world;
        _overlay = overlay;
        return hovered;
    }

    /// <summary>Cadre le sujet de près : la boîte englobante est un majorant prudent de sa taille.</summary>
    void FrameSubject(World world)
    {
        _view.FrameAll(world, minRadius: 10f);
        _view.Distance *= 0.55f;
    }

    /// <summary>Rend l'aperçu demandé à cette image, s'il y en a un, puis oublie le monde (il peut venir du jeu).</summary>
    public void Render(CommandBuffer cmd)
    {
        if (_world is null)
            return;
        _view.Begin();
        _view.Frame.ClearColor = new float4(0.03f, 0.03f, 0.045f, 1);
        _view.DrawEntities(_world);
        if (_overlay is not null)
            foreach (var l in _overlay.Lines)
                _view.Frame.Line(l.A, l.B, l.Color);
        _renderer.Render(cmd, Slot, _view.Frame);
        _world = null;
        _overlay = null;
    }

    /// <summary>Avant un rechargement du jeu : aucune référence vers son monde.</summary>
    public void Forget()
    {
        _world = null;
        _overlay = null;
    }

    public void Dispose() => _renderer.Dispose();
}

/// <summary>
/// Les vignettes du navigateur de contenu, rendues hors écran : scènes (cuites avec le module du jeu), modèles glTF, et
/// fichiers que le jeu sait montrer (API d'extension). Gardées en mémoire et sur disque, dans
/// <c>.poiesis/thumbnails/</c>, et refaites quand le fichier ou le jeu change. Une vignette par image au plus.
/// </summary>
sealed class ThumbnailService : IDisposable
{
    public const int Size = 192;

    readonly VulkanContext _vk;
    readonly VulkanBackend _backend;
    readonly EditorSession _session;
    readonly ExtensionHost _extensions;
    readonly LogBuffer _log;
    readonly string _contentRoot;
    readonly string _cacheDir;
    readonly SceneRenderer _renderer;
    readonly WorldView _view;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly List<(Texture Texture, ImTextureRef Ref, int Frame)> _retired = new();
    readonly Queue<string> _queue = new();
    int _frame;

    sealed class Entry
    {
        public required string Key;
        public Texture? Texture;
        public ImTextureRef Ref;
        public bool Failed;
    }

    public ThumbnailService(VulkanContext vk, VulkanBackend backend, EditorSession session, ExtensionHost extensions, LogBuffer log, string projectRoot)
    {
        _vk = vk;
        _backend = backend;
        _session = session;
        _extensions = extensions;
        _log = log;
        _contentRoot = Path.Combine(projectRoot, "content");
        _cacheDir = Path.Combine(projectRoot, ".poiesis", "thumbnails");
        _renderer = new SceneRenderer(vk, 1);
        _renderer.Resize(new int2(Size, Size));
        _view = new WorldView(_renderer) { ImageSize = new float2(Size, Size) };
    }

    /// <summary>Nombre de vignettes rendues (pas lues sur disque) depuis le lancement.</summary>
    public int Rendered { get; private set; }

    /// <summary>Vrai si le fichier peut avoir une vignette.</summary>
    public bool Supports(string relativePath) =>
        relativePath.EndsWith(".scene.json", StringComparison.Ordinal) || ModelImporter.IsModel(relativePath)
        || _extensions.ThumbnailFor(Path.GetFileName(relativePath)) is not null;

    /// <summary>La vignette d'un fichier (chemin relatif à <c>content/</c>), ou <c>null</c> tant qu'elle n'est pas prête.</summary>
    public ImTextureRef? Get(string relativePath)
    {
        if (!Supports(relativePath))
            return null;
        var key = KeyOf(relativePath);
        if (key is null)
            return null;
        if (_entries.TryGetValue(relativePath, out var entry) && entry.Key == key)
            return entry.Failed || entry.Texture is null ? null : entry.Ref;
        if (!_queue.Contains(relativePath))
            _queue.Enqueue(relativePath);
        return entry is { Texture: not null } ? entry.Ref : null;   // l'ancienne en attendant la nouvelle
    }

    /// <summary>
    /// Ce qui fait changer la vignette : le fichier, et pour ce que le jeu dessine, la génération du module.
    /// Les données d'une scène (vaisseaux, compositions) ne sont pas suivies : recharger le jeu refait ses vignettes.
    /// </summary>
    string? KeyOf(string relativePath)
    {
        var file = Path.Combine(_contentRoot, relativePath);
        if (!File.Exists(file))
            return null;
        bool needsGame = !ModelImporter.IsModel(relativePath);
        if (needsGame && _session.Modules.Module is null)
            return null;
        return $"v2|{relativePath}|{File.GetLastWriteTimeUtc(file).Ticks}|{(needsGame ? _session.Modules.Generation + "|" + _session.Modules.Module!.Name : "")}";
    }

    /// <summary>À appeler une fois par image, hors de la passe de rendu : produit au plus une vignette.</summary>
    public void Process()
    {
        _frame++;
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_frame - _retired[i].Frame > Swapchain.FramesInFlight + 1)
            {
                _backend.RemoveTexture(_retired[i].Ref);
                _retired[i].Texture.Dispose();
                _retired.RemoveAt(i);
            }

        while (_queue.TryDequeue(out var path))
        {
            var key = KeyOf(path);
            if (key is null || _entries.TryGetValue(path, out var current) && current.Key == key)
                continue;
            var entry = new Entry { Key = key };
            try
            {
                var pixels = LoadCached(key) ?? Render(path, key);
                if (pixels is null)
                    entry.Failed = true;
                else
                {
                    entry.Texture = new Texture(_vk, new int2(Size, Size), pixels);
                    entry.Ref = _backend.AddTexture(_renderer.Sampler, entry.Texture.View, Brutal.VulkanApi.VkImageLayout.ShaderReadOnlyOptimal);
                }
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                _log.Warn("vignettes", $"{path} : {ex.Message}");
            }
            if (_entries.TryGetValue(path, out var old) && old.Texture is not null)
                _retired.Add((old.Texture, old.Ref, _frame));
            _entries[path] = entry;
            return;   // une par image
        }
    }

    string CachePath(string key) =>
        Path.Combine(_cacheDir, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ".png");

    byte[]? LoadCached(string key)
    {
        var file = CachePath(key);
        if (!File.Exists(file))
            return null;
        using var image = Stb.Load(file, 4);
        if (image.Width != Size || image.Height != Size)
            return null;
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.AsSpan<byte4>()).ToArray();
    }

    byte[]? Render(string relativePath, string key)
    {
        var world = BuildWorld(relativePath);
        if (world is null)
            return null;
        _view.Yaw = 2.3f;
        _view.Pitch = 0.5f;
        bool scene = relativePath.EndsWith(".scene.json", StringComparison.Ordinal);
        _view.FrameAll(world, maxRadius: scene ? 10_000 : float.MaxValue, minRadius: 10f);
        if (!scene)
            _view.Distance *= 0.55f;   // un objet seul : la boîte englobante surestime sa taille
        _view.Begin();
        _view.Frame.ClearColor = new float4(0.05f, 0.05f, 0.075f, 1);
        _view.DrawEntities(world);
        var pixels = _renderer.RenderToPixels(_view.Frame);
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        Directory.CreateDirectory(_cacheDir);
        Stb.WritePng(CachePath(key), Size, Size, 4, pixels);
        Rendered++;
        return pixels;
    }

    World? BuildWorld(string relativePath)
    {
        var file = Path.Combine(_contentRoot, relativePath);
        if (ModelImporter.IsModel(relativePath))
        {
            var model = ModelImporter.Load(file);
            var world = new World();
            var library = new MeshLibrary();
            var size = model.Mesh.NormalizeToUnitBox();
            int id = library.Add(new MeshAsset("thumbnail:" + relativePath, model.Mesh, model.BaseColor));
            world.SetResource(library);
            var e = world.Create();
            world.Add(e, new Transform { Position = float3.Zero, Rotation = floatQuat.Identity, Scale = 1 });
            world.Add(e, new Renderable { Mesh = MeshKind.Box, Asset = id, Size = size / MathF.Max(MathF.Max(size.X, size.Y), size.Z) * 100, Color = model.BaseColorFactor });
            return world;
        }
        if (relativePath.EndsWith(".scene.json", StringComparison.Ordinal))
        {
            var module = _session.Modules.Module ?? throw new InvalidOperationException("aucun module chargé");
            var scene = _session.Serializer.Read(File.ReadAllText(file), file);
            var world = new World();
            module.InitializeWorld(world);
            SceneBaker.Bake(scene, world);
            return world;
        }
        return _extensions.BuildThumbnail(Path.GetFileName(relativePath), file);
    }

    /// <summary>Avant un rechargement du jeu : rien à oublier (les mondes ne sont gardés que le temps du rendu), mais les vignettes du jeu seront refaites.</summary>
    public void Dispose()
    {
        _vk.Device.WaitIdle();
        foreach (var e in _entries.Values)
            e.Texture?.Dispose();
        foreach (var r in _retired)
            r.Texture.Dispose();
        _renderer.Dispose();
    }
}
