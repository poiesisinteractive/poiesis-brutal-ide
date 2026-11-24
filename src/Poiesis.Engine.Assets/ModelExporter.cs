using System.Runtime.InteropServices;
using Brutal.GltfApi;
using Brutal.KtxApi;
using Brutal.KtxApi.Abstractions;
using Brutal.Numerics;

namespace Poiesis.Engine.Assets;

/// <summary>
/// Écrit un maillage en glTF binaire (<c>.glb</c>), avec sa texture de couleur en KTX2 compressé Basis (ETC1S,
/// extension KHR_texture_basisu) : le format que lit <see cref="ModelImporter"/>, et que lisent les outils 3D courants.
/// Sert au concepteur de coques (« exporter en glTF ») et aux tests.
/// </summary>
public static class ModelExporter
{
    public static void SaveGlb(MeshData mesh, TextureData? texture, string path, string name = "model")
    {
        var positions = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(mesh.Positions)).ToArray();
        var normals = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(mesh.Normals)).ToArray();
        var uvs = mesh.TexCoords.Count == mesh.Positions.Count ? MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(mesh.TexCoords)).ToArray() : [];
        var indices = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(mesh.Indices)).ToArray();
        var image = texture is null ? [] : EncodeKtx2(texture);

        var buffer = new List<byte>();
        var views = new List<BufferView>();
        int AddView(byte[] bytes)
        {
            while (buffer.Count % 4 != 0)
                buffer.Add(0);   // alignement des accesseurs
            views.Add(new BufferView { Buffer = 0, ByteOffset = buffer.Count, ByteLength = bytes.Length });
            buffer.AddRange(bytes);
            return views.Count - 1;
        }

        var (min, max) = mesh.Bounds();
        var accessors = new List<Accessor>
        {
            new() { BufferView = AddView(positions), ComponentType = Accessor.ComponentTypeEnum.Float, Count = mesh.Positions.Count, Type = Accessor.TypeEnum.Vec3, Min = [min.X, min.Y, min.Z], Max = [max.X, max.Y, max.Z] },
            new() { BufferView = AddView(normals), ComponentType = Accessor.ComponentTypeEnum.Float, Count = mesh.Normals.Count, Type = Accessor.TypeEnum.Vec3 },
        };
        var attributes = new Dictionary<string, int> { ["POSITION"] = 0, ["NORMAL"] = 1 };
        if (uvs.Length > 0)
        {
            accessors.Add(new() { BufferView = AddView(uvs), ComponentType = Accessor.ComponentTypeEnum.Float, Count = mesh.TexCoords.Count, Type = Accessor.TypeEnum.Vec2 });
            attributes["TEXCOORD_0"] = accessors.Count - 1;
        }
        accessors.Add(new() { BufferView = AddView(indices), ComponentType = Accessor.ComponentTypeEnum.UnsignedInt, Count = mesh.Indices.Count, Type = Accessor.TypeEnum.Scalar });
        int indexAccessor = accessors.Count - 1;

        var primitive = new MeshPrimitive { Attributes = attributes, Indices = indexAccessor };
        var model = new Gltf
        {
            Asset = new Asset { Version = "2.0", Generator = "Poiesis" },
            Scene = 0,
            Scenes = [new Scene { Nodes = [0] }],
            Nodes = [new Node { Name = name, Mesh = 0 }],
            Meshes = [new Mesh { Name = name, Primitives = [primitive] }],
            Accessors = [.. accessors],
        };
        if (image.Length > 0)
        {
            int imageView = AddView(image);
            primitive.Material = 0;
            model.Images = [new Image { BufferView = imageView, MimeType = Image.MimeTypeKtx2 }];
            model.Textures = [new Texture { Extensions = new() { ["KHR_texture_basisu"] = new TextureBasisuGltfTextureExtension { Source = 0 } } }];
            model.Materials = [new Material { Name = name, PbrMetallicRoughness = new MaterialPbrMetallicRoughness { BaseColorTexture = new TextureInfo { Index = 0 } } }];
            model.ExtensionsUsed = ["KHR_texture_basisu"];
            model.ExtensionsRequired = ["KHR_texture_basisu"];
        }
        while (buffer.Count % 4 != 0)
            buffer.Add(0);
        model.Buffers = [new Brutal.GltfApi.Buffer { ByteLength = buffer.Count }];
        model.BufferViews = [.. views];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        model.SaveBinaryModel([.. buffer], path);
    }

    /// <summary>Une image RGBA en KTX2 compressé Basis Universal (ETC1S) : petite, et transcodable vers tout GPU.</summary>
    public static byte[] EncodeKtx2(TextureData texture, uint quality = 128)
    {
        var info = new KtxTextureCreateInfo
        {
            VkFormat = (uint)VkFormat.R8G8B8A8Unorm,
            BaseWidth = (uint)texture.Width,
            BaseHeight = (uint)texture.Height,
            BaseDepth = 1,
            NumDimensions = 2,
            NumLevels = 1,
            NumLayers = 1,
            NumFaces = 1,
        };
        Check(KtxTexture2.Create(ref info, KtxTextureCreateStorageEnum.TextureCreateAllocStorage, out var ktx), "création");
        using (ktx)
        {
            Check(ktx.SetImageFromMemory(0, 0, 0, texture.Rgba), "image");
            var basis = new KtxBasisParams { QualityLevel = quality, CompressionLevel = 2, ThreadCount = (uint)Environment.ProcessorCount };
            Check(ktx.CompressBasisEx(in basis), "compression Basis");
            Check(ktx.WriteToMemory(out var bytes), "écriture");
            using (bytes)
                return bytes.Span.ToArray();
        }
    }

    static void Check(KtxErrorCode code, string what)
    {
        if (code != KtxErrorCode.Success)
            throw new InvalidOperationException($"KTX2 ({what}) : {code}");
    }
}
