using Brutal.Numerics;
using Poiesis.Engine.Assets;

namespace Poiesis.Engine.Tests;

public sealed class ModelImportTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "poiesis-model-" + Guid.NewGuid().ToString("N"));

    public ModelImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static MeshData TexturedBox()
    {
        var m = new MeshData();
        m.Loft([new(-1, -0.5f, -2), new(1, -0.5f, -2), new(1, 0.5f, -2), new(-1, 0.5f, -2)],
               [new(-1, -0.5f, 2), new(1, -0.5f, 2), new(1, 0.5f, 2), new(-1, 0.5f, 2)]);
        foreach (var p in m.Positions)
            m.TexCoords.Add(new float2(p.X * 0.5f + 0.5f, p.Z * 0.25f + 0.5f));
        return m;
    }

    static TextureData Checker(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = (x / 8 + y / 8) % 2 == 0 ? (byte)230 : (byte)40;
                rgba.AsSpan((y * size + x) * 4, 4).Fill(v);
                rgba[(y * size + x) * 4 + 3] = 255;
            }
        return new TextureData(size, size, rgba);
    }

    [Fact]
    public void A_glb_with_a_basis_ktx2_texture_round_trips_through_export_and_import()
    {
        var path = Path.Combine(_dir, "box.glb");
        var source = TexturedBox();
        ModelExporter.SaveGlb(source, Checker(64), path, "box");

        var model = ModelImporter.Load(path);

        Assert.Equal(source.TriangleCount, model.Mesh.TriangleCount);
        Assert.Equal(source.Positions.Count, model.Mesh.TexCoords.Count);
        var (min, max) = model.Mesh.Bounds();
        Assert.Equal(new float3(-1, -0.5f, -2), min);
        Assert.Equal(new float3(1, 0.5f, 2), max);
        Assert.True(model.Stats.AcmrAfter <= model.Stats.AcmrBefore);

        // La texture a été compressée en Basis (ETC1S) puis transcodée en RGBA : le damier reste lisible.
        var tex = Assert.IsType<TextureData>(model.BaseColor);
        Assert.Equal((64, 64), (tex.Width, tex.Height));
        Assert.Equal("KTX2", model.Stats.TextureFormat);
        Assert.True(tex.Rgba[0] > 150 && tex.Rgba[(8 * 4)] < 100, $"damier altéré : {tex.Rgba[0]}, {tex.Rgba[32]}");
    }

    [Fact]
    public void Normalizing_to_the_unit_box_returns_the_original_size()
    {
        var m = TexturedBox();
        var size = m.NormalizeToUnitBox();
        Assert.Equal(new float3(2, 1, 4), size);
        var (min, max) = m.Bounds();
        Assert.Equal(new float3(-0.5f), min);
        Assert.Equal(new float3(0.5f), max);
    }

    [Fact]
    public void The_mesh_library_numbers_assets_from_one_and_replaces_by_key()
    {
        var lib = new MeshLibrary();
        var a = lib.Add(new MeshAsset("a", new MeshData()));
        var b = lib.Add(new MeshAsset("b", new MeshData()));
        var replacement = new MeshAsset("a", TexturedBox());
        Assert.Equal((1, 2), (a, b));
        Assert.Equal(1, lib.Add(replacement));
        Assert.Same(replacement, lib.Get(1));
        Assert.Null(lib.Get(0));
        Assert.Equal(2, lib.IdOf("b"));
    }
}
