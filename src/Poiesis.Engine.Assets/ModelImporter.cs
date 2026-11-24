using System.Runtime.InteropServices;
using System.Text.Json;
using Brutal;
using Brutal.GltfApi;
using Brutal.KtxApi;
using Brutal.MeshOptimizer;
using Brutal.Numerics;
using Brutal.TextureApi;

namespace Poiesis.Engine.Assets;

/// <summary>Un modèle importé : un maillage (tous les nœuds de la scène fusionnés), sa texture de couleur et sa teinte.</summary>
public sealed record ModelData(MeshData Mesh, TextureData? BaseColor, float4 BaseColorFactor, ImportStats Stats);

/// <summary>Ce que l'import a fait : nombre de primitives, de sommets, de triangles, et l'efficacité du cache de sommets.</summary>
public sealed record ImportStats(int Primitives, int Vertices, int Triangles, float AcmrBefore, float AcmrAfter, string? TextureFormat);

/// <summary>
/// Import de modèles glTF 2.0 (<c>.gltf</c>, <c>.glb</c>) pour le jeu et l'éditeur :
/// <list type="bullet">
/// <item>les nœuds de la scène, avec leurs transformations, sont fusionnés en un seul maillage ;</item>
/// <item>positions, normales (calculées si absentes), coordonnées de texture (TEXCOORD_0) et indices ;</item>
/// <item>la première texture de couleur de base trouvée, en PNG, JPEG ou KTX2 (KHR_texture_basisu, transcodée en RGBA) ;</item>
/// <item>triangles puis sommets réordonnés par meshoptimizer pour le cache et la lecture du GPU.</item>
/// </list>
/// Une seule texture par modèle pour l'instant : les modèles à plusieurs matériaux prennent celle du premier.
/// </summary>
public static class ModelImporter
{
    public static bool IsModel(string path) =>
        path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public float3 Position;
        public float3 Normal;
        public float2 TexCoord;
    }

    public static ModelData Load(string path)
    {
        using var loader = new GltfLoader(path);
        var gltf = loader.GltfJson;
        var vertices = new List<Vertex>();
        var indices = new List<int>();
        int primitives = 0;
        int? textureIndex = null;
        var factor = new float4(1);

        void AddMesh(int meshIndex, float4x4 world)
        {
            var normalMatrix = world;
            foreach (var primitive in gltf.Meshes![meshIndex].Primitives)
            {
                if (primitive.Mode != MeshPrimitive.ModeEnum.Triangles || !primitive.Attributes.TryGetValue("POSITION", out var posAccessor))
                    continue;
                var positions = ReadFloat3s(loader, posAccessor);
                int[] ids = primitive.Indices is { } i ? ReadIndices(loader, i) : [.. Enumerable.Range(0, positions.Length)];
                var normals = primitive.Attributes.TryGetValue("NORMAL", out var n) ? ReadFloat3s(loader, n) : SmoothNormals(positions, ids);
                var uvs = primitive.Attributes.TryGetValue("TEXCOORD_0", out var t) ? ReadFloat2s(loader, t) : null;

                if (primitive.Material is { } m && gltf.Materials?[m].PbrMetallicRoughness is { } pbr)
                {
                    if (textureIndex is null && pbr.BaseColorTexture is { } info)
                        textureIndex = info.Index;
                    if (primitives == 0)
                        factor = pbr.BaseColorFactor;
                }

                int baseVertex = vertices.Count;
                for (int v = 0; v < positions.Length; v++)
                    vertices.Add(new Vertex
                    {
                        Position = float3.Transform(positions[v], world),
                        Normal = float3.NormalizeOrZero(float3.TransformNormal(normals[v], normalMatrix)),
                        TexCoord = uvs is not null && v < uvs.Length ? uvs[v] : default,
                    });
                foreach (var id in ids)
                    indices.Add(baseVertex + id);
                primitives++;
            }
        }

        void AddNode(int nodeIndex, float4x4 parent)
        {
            var node = gltf.Nodes[nodeIndex];
            var trs = float4x4.CreateScale(node.Scale) * float4x4.CreateFromQuaternion(node.Rotation) * float4x4.CreateTranslation(node.Translation);
            var world = node.Matrix * trs * parent;
            if (node.Mesh is { } mesh)
                AddMesh(mesh, world);
            foreach (var child in node.Children ?? [])
                AddNode(child, world);
        }

        if ((gltf.Scene ?? (gltf.Scenes?.Length > 0 ? 0 : null)) is { } scene)
            foreach (var root in gltf.Scenes![scene].Nodes ?? [])
                AddNode(root, float4x4.Identity);
        else
            for (int mesh = 0; mesh < (gltf.Meshes?.Length ?? 0); mesh++)
                AddMesh(mesh, float4x4.Identity);

        if (indices.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} : aucun maillage en triangles");

        var (before, after) = Optimize(vertices, indices);

        var data = new MeshData();
        foreach (var v in vertices)
        {
            data.Positions.Add(v.Position);
            data.Normals.Add(v.Normal);
            data.TexCoords.Add(v.TexCoord);
        }
        foreach (var i in indices)
            data.Indices.Add((uint)i);

        TextureData? texture = null;
        string? format = null;
        if (textureIndex is { } ti && ImageOf(gltf, ti) is { } imageIndex)
            (texture, format) = LoadImage(loader, imageIndex);

        return new ModelData(data, texture, factor, new ImportStats(primitives, vertices.Count, indices.Count / 3, before, after, format));
    }

    /// <summary>L'image d'une texture : celle de KHR_texture_basisu (KTX2) si elle existe, sinon la source ordinaire.</summary>
    static int? ImageOf(Gltf gltf, int textureIndex)
    {
        var texture = gltf.Textures[textureIndex];
        if (texture.Extensions?.TryGetValue("KHR_texture_basisu", out var ext) == true)
        {
            switch (ext)
            {
                case JsonElement e when e.TryGetProperty("source"u8, out var s) && s.TryGetInt32(out var source):
                    return source;
                case TextureBasisuGltfTextureExtension b when b.Source is { } source:
                    return source;
            }
        }
        return texture.Source;
    }

    static (TextureData?, string?) LoadImage(GltfLoader loader, int imageIndex)
    {
        var image = loader.GltfJson.Images[imageIndex];
        byte[] bytes;
        string kind;
        if (image.BufferView is { } view)
        {
            bytes = loader.GetBufferView(view).ToArray();
            kind = image.MimeType ?? "";
        }
        else if (image.Uri is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal))
        {
            var file = Path.Combine(Path.GetDirectoryName(loader.FilePath) ?? "", Uri.UnescapeDataString(uri));
            bytes = File.ReadAllBytes(file);
            kind = Path.GetExtension(file);
        }
        else
        {
            return (null, null);
        }
        var type = kind switch
        {
            Image.MimeTypePng or ".png" => TextureLoader.FormatType.Png,
            Image.MimeTypeJpeg or ".jpg" or ".jpeg" => TextureLoader.FormatType.Jpg,
            Image.MimeTypeKtx2 or ".ktx2" => TextureLoader.FormatType.Ktx2,
            _ => throw new NotSupportedException($"Image de type « {kind} » non prise en charge (PNG, JPEG ou KTX2)"),
        };
        return (DecodeRgba(bytes, type), type.ToString().ToUpperInvariant());
    }

    /// <summary>Charge une image PNG, JPEG ou KTX2 en RGBA 8 bits (les KTX2 Basis sont transcodés).</summary>
    public static TextureData LoadTexture(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var type = ext switch
        {
            ".png" => TextureLoader.FormatType.Png,
            ".jpg" or ".jpeg" => TextureLoader.FormatType.Jpg,
            ".ktx2" => TextureLoader.FormatType.Ktx2,
            _ => throw new NotSupportedException($"Texture « {ext} » non prise en charge (PNG, JPEG ou KTX2)"),
        };
        return DecodeRgba(File.ReadAllBytes(path), type);
    }

    static TextureData DecodeRgba(ReadOnlySpan<byte> bytes, TextureLoader.FormatType type)
    {
        var texture = type == TextureLoader.FormatType.Ktx2 ? LoadKtx2(bytes) : TextureLoader.LoadFromMemory(bytes, type,
            Brutal.TextureApi.Stb.Loader.LoadSettings.ForceRgba8,
            new Brutal.TextureApi.Ktx.Loader.LoadSettings { SuperCompressionTranscodeFormat = Brutal.KtxApi.KtxTranscodeFmt.Rgba32 });
        try
        {
            if (texture.Format is not (TextureFormat.R8G8B8A8_UNorm or TextureFormat.R8G8B8A8_SRGB))
                throw new NotSupportedException($"Format de texture {texture.Format} : seul le RGBA 8 bits est pris en charge");
            int w = (int)texture.Width, h = (int)texture.Height;
            return new TextureData(w, h, texture.ImageData(0, 0, 0)[..(w * h * 4)].ToArray());
        }
        finally
        {
            (texture as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// KTX2 depuis la mémoire, transcodé en RGBA s'il est compressé Basis. (Le chargeur de Brutal.Texture ne transcode
    /// qu'à la lecture d'un fichier ; une texture intégrée au .glb n'en est pas un.)
    /// </summary>
    static ITexture LoadKtx2(ReadOnlySpan<byte> bytes)
    {
        var result = KtxTexture2.CreateFromMemory(bytes, KtxTextureCreateFlagBits.LoadImageDataBit, out var handle);
        if (result != KtxErrorCode.Success)
            throw new InvalidDataException($"KTX2 illisible : {result}");
        KtxTextureHandle common = handle;
        if (common.NeedsTranscoding())
        {
            result = handle.TranscodeBasis(KtxTranscodeFmt.Rgba32, KtxTranscodeFlagBits.HighQuality);
            if (result != KtxErrorCode.Success)
            {
                handle.Dispose();
                throw new InvalidDataException($"KTX2 : transcodage Basis impossible ({result})");
            }
        }
        return new Brutal.TextureApi.Ktx.KtxTexture(common);
    }

    /// <summary>Réordonne triangles puis sommets pour le cache de sommets et la lecture du GPU (meshoptimizer). Rend l'ACMR avant et après.</summary>
    static unsafe (float Before, float After) Optimize(List<Vertex> vertices, List<int> indices)
    {
        var ids = indices.ToArray();
        var verts = vertices.ToArray();
        float before, after;
        fixed (int* p = ids)
        fixed (Vertex* v = verts)
        {
            before = MeshOptimizer.AnalyzeVertexCache(p, ids.Length, verts.Length, 16, 0, 0).Acmr;
            MeshOptimizer.OptimizeVertexCache(p, p, ids.Length, verts.Length);
            MeshOptimizer.OptimizeVertexFetch(v, p, ids.Length, v, verts.Length, ByteSize.Of<Vertex>());
            after = MeshOptimizer.AnalyzeVertexCache(p, ids.Length, verts.Length, 16, 0, 0).Acmr;
        }
        indices.Clear();
        indices.AddRange(ids);
        vertices.Clear();
        vertices.AddRange(verts);
        return (before, after);
    }

    static ReadOnlySpan<byte> AccessorBytes(GltfLoader loader, Accessor accessor, out int stride, int defaultStride)
    {
        var view = loader.GltfJson.BufferViews[accessor.BufferView!.Value];
        stride = view.ByteStride ?? defaultStride;
        return loader.GetBufferView(accessor.BufferView.Value)[accessor.ByteOffset..];
    }

    static float3[] ReadFloat3s(GltfLoader loader, int accessorIndex)
    {
        var accessor = loader.GltfJson.Accessors[accessorIndex];
        if (accessor.ComponentType != Accessor.ComponentTypeEnum.Float || accessor.Type != Accessor.TypeEnum.Vec3)
            throw new NotSupportedException($"Attendu des VEC3 flottants, pas {accessor.ComponentType} {accessor.Type}");
        var bytes = AccessorBytes(loader, accessor, out var stride, 12);
        var result = new float3[accessor.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = MemoryMarshal.Read<float3>(bytes[(i * stride)..]);
        return result;
    }

    /// <summary>Coordonnées de texture : flottants, ou entiers normalisés (8 ou 16 bits) comme le permet glTF.</summary>
    static float2[] ReadFloat2s(GltfLoader loader, int accessorIndex)
    {
        var accessor = loader.GltfJson.Accessors[accessorIndex];
        var (size, scale) = accessor.ComponentType switch
        {
            Accessor.ComponentTypeEnum.Float => (4, 1f),
            Accessor.ComponentTypeEnum.UnsignedShort => (2, 1f / ushort.MaxValue),
            Accessor.ComponentTypeEnum.UnsignedByte => (1, 1f / byte.MaxValue),
            _ => throw new NotSupportedException($"Coordonnées de texture en {accessor.ComponentType}"),
        };
        var bytes = AccessorBytes(loader, accessor, out var stride, size * 2);
        var result = new float2[accessor.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var at = bytes[(i * stride)..];
            result[i] = size switch
            {
                4 => MemoryMarshal.Read<float2>(at),
                2 => new float2(MemoryMarshal.Read<ushort>(at), MemoryMarshal.Read<ushort>(at[2..])) * scale,
                _ => new float2(at[0], at[1]) * scale,
            };
        }
        return result;
    }

    static int[] ReadIndices(GltfLoader loader, int accessorIndex)
    {
        var accessor = loader.GltfJson.Accessors[accessorIndex];
        var bytes = loader.GetBufferView(accessor.BufferView!.Value)[accessor.ByteOffset..];
        return accessor.ComponentType switch
        {
            Accessor.ComponentTypeEnum.UnsignedByte => [.. bytes[..accessor.Count].ToArray().Select(b => (int)b)],
            Accessor.ComponentTypeEnum.UnsignedShort => [.. MemoryMarshal.Cast<byte, ushort>(bytes)[..accessor.Count].ToArray().Select(s => (int)s)],
            _ => MemoryMarshal.Cast<byte, int>(bytes)[..accessor.Count].ToArray(),
        };
    }

    static float3[] SmoothNormals(float3[] positions, int[] indices)
    {
        var normals = new float3[positions.Length];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var normal = float3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]);
            normals[indices[t]] += normal;
            normals[indices[t + 1]] += normal;
            normals[indices[t + 2]] += normal;
        }
        return [.. normals.Select(float3.NormalizeOrZero)];
    }
}
