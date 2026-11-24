using System.Runtime.InteropServices;
using Brutal;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Poiesis.Render.Vulkan;

/// <summary>
/// Rendu 3D du viewport, hors écran, en rendu dynamique Vulkan 1.3 : maillages instanciés éclairés (une passe par
/// maillage, toutes ses instances d'un coup), puis lignes (grille, axes, gizmos). Une cible par image en vol, pour ne
/// jamais réécrire une texture qu'ImGui est encore en train de lire.
/// </summary>
public sealed unsafe class SceneRenderer : IDisposable
{
    public const VkFormat ColorFormat = VkFormat.R8G8B8A8UNorm;
    public const VkFormat DepthFormat = VkFormat.D32SFloat;

    [StructLayout(LayoutKind.Sequential)]
    struct MeshVertex
    {
        public float3 Position;
        public float3 Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InstanceData
    {
        public float4x4 World;
        public float4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PushConstants
    {
        public float4x4 ViewProjection;
        public float4 LightDirection;
    }

    sealed class GpuBuffer
    {
        public VkBuffer Buffer;
        public VmaAllocation Allocation;
        public nint Mapped;
        public ulong Capacity;
    }

    sealed class GpuMesh
    {
        public required GpuBuffer Vertices;
        public required GpuBuffer Indices;
        public int IndexCount;
    }

    sealed class Target
    {
        public VkImage Color;
        public VmaAllocation ColorAlloc;
        public VkImageView ColorView;
        public VkImage Depth;
        public VmaAllocation DepthAlloc;
        public VkImageView DepthView;
        public VkImageLayout ColorLayout;
        public GpuBuffer? InstanceBuffer;
        public GpuBuffer? LineBuffer;
    }

    readonly VulkanContext _vk;
    readonly List<GpuMesh> _meshes = new();
    readonly Target[] _targets;
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _meshPipeline;
    readonly VkPipeline _linePipeline;
    InstanceData[] _instanceScratch = new InstanceData[256];

    public SceneRenderer(VulkanContext vk, int framesInFlight)
    {
        _vk = vk;
        _targets = Enumerable.Range(0, framesInFlight).Select(_ => new Target()).ToArray();
        Sampler = vk.Device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxLod = 1,
        }, null);

        var range = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Size = (ByteSize32)sizeof(PushConstants),
        };
        _layout = vk.Device.CreatePipelineLayout(new VkPipelineLayoutCreateInfo
        {
            PushConstantRangeCount = 1,
            PushConstantRanges = &range,
        }, null);

        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        _meshPipeline = CreatePipeline(compiler, options, Shaders.MeshVertex, Shaders.MeshFragment,
            new VertexInput(2, 6)
                .AddBindingWithAttributes<MeshVertex>(0, 0, VkVertexInputRate.Vertex)
                .AddBindingWithAttributes<InstanceData>(2, 1, VkVertexInputRate.Instance),
            Presets.InputAssembly.TriangleList, depthWrite: true);
        _linePipeline = CreatePipeline(compiler, options, Shaders.LineVertex, Shaders.LineFragment,
            new VertexInput(1, 2).AddBindingWithAttributes<LineVertex>(0, 0, VkVertexInputRate.Vertex),
            Presets.InputAssembly.LineList, depthWrite: false);
    }

    public VkSampler Sampler { get; }

    public int2 Size { get; private set; }

    /// <summary>Incrémenté à chaque recréation des cibles : l'éditeur réenregistre alors ses textures ImGui.</summary>
    public int TargetVersion { get; private set; }

    public VkImageView ColorView(int slot) => _targets[slot].ColorView;

    public MeshHandle UploadMesh(ReadOnlySpan<float3> positions, ReadOnlySpan<float3> normals, ReadOnlySpan<uint> indices)
    {
        var vertices = new MeshVertex[positions.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new MeshVertex { Position = positions[i], Normal = normals[i] };

        var vb = CreateBuffer((ulong)(vertices.Length * sizeof(MeshVertex)), VkBufferUsageFlags.VertexBufferBit);
        MemoryMarshal.AsBytes(vertices.AsSpan()).CopyTo(new Span<byte>((void*)vb.Mapped, vertices.Length * sizeof(MeshVertex)));
        var ib = CreateBuffer((ulong)(indices.Length * sizeof(uint)), VkBufferUsageFlags.IndexBufferBit);
        MemoryMarshal.AsBytes(indices).CopyTo(new Span<byte>((void*)ib.Mapped, indices.Length * sizeof(uint)));

        _meshes.Add(new GpuMesh { Vertices = vb, Indices = ib, IndexCount = indices.Length });
        return new MeshHandle(_meshes.Count - 1);
    }

    /// <summary>Redimensionne les cibles (pixels). Attend le GPU : à n'appeler que si la taille change.</summary>
    public void Resize(int2 size)
    {
        size = new int2(Math.Max(size.X, 1), Math.Max(size.Y, 1));
        if (size == Size)
            return;
        _vk.Device.WaitIdle();
        foreach (var t in _targets)
        {
            DestroyImages(t);
            CreateImages(t, size);
        }
        Size = size;
        TargetVersion++;
    }

    /// <summary>
    /// Enregistre le rendu d'une image dans la cible <paramref name="slot"/>. À la fin, la cible est prête à être
    /// échantillonnée par ImGui (ShaderReadOnlyOptimal).
    /// </summary>
    public void Render(CommandBuffer cmd, int slot, RenderFrame frame)
    {
        if (Size.X == 0)
            return;
        var t = _targets[slot];

        // Données d'instances et de lignes de cette image, dans les tampons de ce slot.
        int instanceCount = frame.Batches.Values.Sum(b => b.Count);
        if (_instanceScratch.Length < instanceCount)
            _instanceScratch = new InstanceData[Math.Max(instanceCount, _instanceScratch.Length * 2)];
        int n = 0;
        foreach (var batch in frame.Batches.Values)
            foreach (var inst in batch)
                _instanceScratch[n++] = new InstanceData { World = inst.World, Color = inst.Color };
        EnsureCapacity(ref t.InstanceBuffer, (ulong)Math.Max(n, 1) * (ulong)sizeof(InstanceData), VkBufferUsageFlags.VertexBufferBit);
        MemoryMarshal.AsBytes(_instanceScratch.AsSpan(0, n)).CopyTo(new Span<byte>((void*)t.InstanceBuffer!.Mapped, n * sizeof(InstanceData)));

        var lines = CollectionsMarshal.AsSpan(frame.Lines);
        EnsureCapacity(ref t.LineBuffer, (ulong)Math.Max(lines.Length, 2) * (ulong)sizeof(LineVertex), VkBufferUsageFlags.VertexBufferBit);
        MemoryMarshal.AsBytes(lines).CopyTo(new Span<byte>((void*)t.LineBuffer!.Mapped, lines.Length * sizeof(LineVertex)));

        Swapchain.ImageBarrier(cmd, t.Color, t.ColorLayout, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ShaderReadBit, VkAccessFlags.ColorAttachmentWriteBit);
        Swapchain.ImageBarrier(cmd, t.Depth, VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkAccessFlags.DepthStencilAttachmentWriteBit, VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkImageAspectFlags.DepthBit);

        var color = new VkRenderingAttachmentInfo
        {
            ImageView = t.ColorView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearValue { Color = new VkClearColorValue { Float32 = frame.ClearColor } },
        };
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = t.DepthView,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f } },
        };
        var extent = new VkExtent2D(Size.X, Size.Y);
        cmd.BeginRendering(new VkRenderingInfo
        {
            RenderArea = new VkRect2D(extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
            DepthAttachment = &depth,
        });

        // Hauteur négative : on garde la convention « y vers le haut » des matrices de Brutal.Numerics.
        cmd.SetViewport(0, [new VkViewport { X = 0, Y = Size.Y, Width = Size.X, Height = -Size.Y, MaxDepth = 1 }]);
        cmd.SetScissor(0, [new VkRect2D(extent)]);

        var push = new PushConstants
        {
            ViewProjection = frame.Camera.View * frame.Camera.Projection(Size.X / (float)Size.Y),
            LightDirection = new float4(float3.Normalize(frame.LightDirection), 0),
        };
        var pushBytes = new ReadOnlySpan<byte>(&push, sizeof(PushConstants));

        if (n > 0)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _meshPipeline);
            cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, pushBytes);
            int first = 0;
            foreach (var (handle, batch) in frame.Batches)
            {
                if (batch.Count == 0)
                    continue;
                var mesh = _meshes[handle.Value];
                cmd.BindVertexBuffers(0, [mesh.Vertices.Buffer, t.InstanceBuffer.Buffer],
                    [new ByteSize64(0), new ByteSize64((ulong)(first * sizeof(InstanceData)))]);
                cmd.BindIndexBuffer(mesh.Indices.Buffer, new ByteSize64(0), VkIndexType.UInt32);
                cmd.DrawIndexed(mesh.IndexCount, batch.Count, 0, 0, 0);
                first += batch.Count;
            }
        }

        if (lines.Length > 0)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _linePipeline);
            cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, pushBytes);
            cmd.BindVertexBuffers(0, [t.LineBuffer.Buffer], [new ByteSize64(0)]);
            cmd.Draw(lines.Length, 1, 0, 0);
        }

        cmd.EndRendering();

        Swapchain.ImageBarrier(cmd, t.Color, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
        t.ColorLayout = VkImageLayout.ShaderReadOnlyOptimal;
    }

    VkPipeline CreatePipeline(Compiler compiler, CompileOptions options, ReadOnlySpan<byte> vert, ReadOnlySpan<byte> frag,
        VertexInput input, PtrOwner<VkPipelineInputAssemblyStateCreateInfo> assembly, bool depthWrite)
    {
        VkShaderModule Compile(ReadOnlySpan<byte> glsl, ShaderKind kind)
        {
            using var spirv = compiler.Compile(glsl, kind, "poiesis"u8, "main"u8, options);
            if (spirv.GetCompilationStatus() != CompilationStatus.Success)
                throw new InvalidOperationException($"Shader : {spirv.GetErrorMessage()}");
            return ShaderModuleExtensions.FromSpirv(_vk.Device, new ReadOnlySpan<byte>(spirv.GetBytes(), (int)spirv.GetLength()), null);
        }

        using var stages = new ShaderStages(_vk.Device,
        [
            Presets.ShaderStage.Vertex(Compile(vert, ShaderKind.VertexShader)),
            Presets.ShaderStage.Fragment(Compile(frag, ShaderKind.FragmentShader)),
        ], null);

        var colorFormat = ColorFormat;
        var rendering = new VkPipelineRenderingCreateInfo
        {
            ColorAttachmentCount = 1,
            ColorAttachmentFormats = &colorFormat,
            DepthAttachmentFormat = DepthFormat,
        };
        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
        var depth = new VkPipelineDepthStencilStateCreateInfo
        {
            DepthTestEnable = true,
            DepthWriteEnable = depthWrite,
            DepthCompareOp = VkCompareOp.LessOrEqual,
        };

        return _vk.Device.CreateGraphicsPipeline(VkPipelineCache.NullHandle, new VkGraphicsPipelineCreateInfo
        {
            Next = &rendering,
            StageCount = stages.Count,
            Stages = stages,
            VertexInputState = input,
            InputAssemblyState = assembly,
            ViewportState = &viewportState,
            RasterizationState = Presets.Rasterization.Fill.CullNone,
            MultisampleState = Presets.Multisample.None,
            DepthStencilState = &depth,
            ColorBlendState = Presets.BlendState.BlendColorAlpha,
            DynamicState = &dynamic,
            Layout = _layout,
        }, null);
    }

    GpuBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage)
    {
        _vk.Allocator.CreateBuffer(
            new VkBufferCreateInfo { Size = new ByteSize64(size), Usage = usage },
            new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
            },
            out var buffer, out var allocation, out var info);
        return new GpuBuffer { Buffer = buffer, Allocation = allocation, Mapped = (nint)info.PMappedData, Capacity = size };
    }

    void EnsureCapacity(ref GpuBuffer? buffer, ulong size, VkBufferUsageFlags usage)
    {
        if (buffer is not null && buffer.Capacity >= size)
            return;
        // Ce tampon appartient à un slot dont l'image précédente est terminée (barrière de la swapchain) : on peut le détruire.
        if (buffer is not null)
            _vk.Allocator.DestroyBuffer(buffer.Buffer, buffer.Allocation);
        buffer = CreateBuffer(Math.Max(size, (buffer?.Capacity ?? 0) * 2), usage);
    }

    void CreateImages(Target t, int2 size)
    {
        var alloc = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto };
        _vk.Allocator.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = ColorFormat,
            Extent = new VkExtent3D(size.X, size.Y, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit,
            InitialLayout = VkImageLayout.Undefined,
        }, alloc, out t.Color, out t.ColorAlloc, out _);
        _vk.Allocator.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = DepthFormat,
            Extent = new VkExtent3D(size.X, size.Y, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
            InitialLayout = VkImageLayout.Undefined,
        }, alloc, out t.Depth, out t.DepthAlloc, out _);

        t.ColorView = _vk.Device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = t.Color,
            ViewType = VkImageViewType._2D,
            Format = ColorFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);
        t.DepthView = _vk.Device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = t.Depth,
            ViewType = VkImageViewType._2D,
            Format = DepthFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = 1 },
        }, null);

        // ImGui peut afficher la cible avant le premier rendu : on la met tout de suite en lecture.
        var color = t.Color;
        _vk.Immediate(cmd => Swapchain.ImageBarrier(cmd, color, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.None, VkAccessFlags.ShaderReadBit));
        t.ColorLayout = VkImageLayout.ShaderReadOnlyOptimal;
    }

    void DestroyImages(Target t)
    {
        if (t.Color.IsNull())
            return;
        _vk.Device.DestroyImageView(t.ColorView, null);
        _vk.Device.DestroyImageView(t.DepthView, null);
        _vk.Allocator.DestroyImage(t.Color, t.ColorAlloc);
        _vk.Allocator.DestroyImage(t.Depth, t.DepthAlloc);
        t.Color = default;
    }

    public void Dispose()
    {
        _vk.Device.WaitIdle();
        foreach (var t in _targets)
        {
            DestroyImages(t);
            foreach (var b in new[] { t.InstanceBuffer, t.LineBuffer })
                if (b is not null)
                    _vk.Allocator.DestroyBuffer(b.Buffer, b.Allocation);
        }
        foreach (var m in _meshes)
        {
            _vk.Allocator.DestroyBuffer(m.Vertices.Buffer, m.Vertices.Allocation);
            _vk.Allocator.DestroyBuffer(m.Indices.Buffer, m.Indices.Allocation);
        }
        _vk.Device.DestroyPipeline(_meshPipeline, null);
        _vk.Device.DestroyPipeline(_linePipeline, null);
        _vk.Device.DestroyPipelineLayout(_layout, null);
        _vk.Device.DestroySampler(Sampler, null);
    }
}
