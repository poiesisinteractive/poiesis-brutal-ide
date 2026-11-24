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
/// Le post-traitement HDR : la scène est rendue dans une cible flottante (RGBA 16 bits), puis le bloom (réductions
/// successives avec seuil doux, puis agrandissements additifs) et le tonemapping l'amènent dans la cible 8 bits
/// qu'affiche ImGui. Le tonemapping laisse intactes les couleurs sous 0,8 (interface, grille) et comprime au-delà.
/// </summary>
sealed unsafe class PostProcess : IDisposable
{
    public const VkFormat HdrFormat = VkFormat.R16G16B16A16SFloat;
    public const int Levels = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct PostPush
    {
        public float4 Params;   // x : texel x, y : texel y, z : seuil ou intensité, w : exposition
    }

    sealed class Image
    {
        public VkImage Handle;
        public VmaAllocation Allocation;
        public VkImageView View;
        public VkImageLayout Layout;
        public int2 Size;
    }

    sealed class Chain
    {
        public Image Hdr = new();
        public Image[] Bloom = Enumerable.Range(0, Levels).Select(_ => new Image()).ToArray();
        public VkDescriptorSet HdrSet;
        public VkDescriptorSet[] BloomSets = new VkDescriptorSet[Levels];
        public VkDescriptorSet CompositeSet;
    }

    readonly VulkanContext _vk;
    readonly Chain[] _chains;
    readonly VkSampler _sampler;
    readonly DescriptorSetLayoutEx _setLayout;
    readonly DescriptorPoolEx _pool;
    readonly VkPipelineLayout _layout;
    readonly VkPipeline _down, _downFirst, _up, _composite;

    public PostProcess(VulkanContext vk, int targets, Compiler compiler, CompileOptions options, Func<Compiler, CompileOptions, ReadOnlySpan<byte>, ShaderKind, VkShaderModule> compile)
    {
        _vk = vk;
        _chains = Enumerable.Range(0, targets).Select(_ => new Chain()).ToArray();
        _sampler = vk.Device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxLod = 1,
        }, null);
        var binding = new VkDescriptorSetLayoutBinding { DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        _setLayout = vk.Device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings = [binding, binding with { Binding = 1 }],
        }, null);
        int sets = targets * (Levels + 2);
        _pool = vk.Device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = sets,
            PoolSizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = sets * 2 }],
        }, null);
        foreach (var c in _chains)
        {
            c.HdrSet = vk.Device.AllocateDescriptorSet(_pool, _setLayout);
            for (int i = 0; i < Levels; i++)
                c.BloomSets[i] = vk.Device.AllocateDescriptorSet(_pool, _setLayout);
            c.CompositeSet = vk.Device.AllocateDescriptorSet(_pool, _setLayout);
        }

        var range = new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Size = (ByteSize32)sizeof(PostPush) };
        var setLayout = (VkDescriptorSetLayout)_setLayout;
        _layout = vk.Device.CreatePipelineLayout(new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PushConstantRanges = &range,
        }, null);

        var vertex = compile(compiler, options, Shaders.FullscreenVertex, ShaderKind.VertexShader);
        try
        {
            var none = new VkPipelineColorBlendAttachmentState { ColorWriteMask = (VkColorComponentFlags)0xF };
            var add = none with
            {
                BlendEnable = true,
                SrcColorBlendFactor = VkBlendFactor.One,
                DstColorBlendFactor = VkBlendFactor.One,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.One,
                AlphaBlendOp = VkBlendOp.Add,
            };
            _downFirst = Pipeline(vertex, compile(compiler, options, Shaders.BloomPrefilter, ShaderKind.FragmentShader), HdrFormat, none);
            _down = Pipeline(vertex, compile(compiler, options, Shaders.BloomDown, ShaderKind.FragmentShader), HdrFormat, none);
            _up = Pipeline(vertex, compile(compiler, options, Shaders.BloomUp, ShaderKind.FragmentShader), HdrFormat, add);
            _composite = Pipeline(vertex, compile(compiler, options, Shaders.Composite, ShaderKind.FragmentShader), SceneRenderer.ColorFormat, none);
        }
        finally
        {
            vk.Device.DestroyShaderModule(vertex, null);
        }
    }

    VkPipeline Pipeline(VkShaderModule vertex, VkShaderModule fragment, VkFormat format, VkPipelineColorBlendAttachmentState blend)
    {
        try
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = vertex, Name = (byte*)Marshal.StringToHGlobalAnsi("main") };
            stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = fragment, Name = stages[0].Name };
            var rendering = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = &format };
            var input = new VkPipelineVertexInputStateCreateInfo();
            var assembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo { PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, LineWidth = 1 };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var blendState = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
            var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
            var depth = new VkPipelineDepthStencilStateCreateInfo();
            var pipeline = _vk.Device.CreateGraphicsPipeline(VkPipelineCache.NullHandle, new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering,
                StageCount = 2,
                Stages = stages,
                VertexInputState = &input,
                InputAssemblyState = &assembly,
                ViewportState = &viewportState,
                RasterizationState = &raster,
                MultisampleState = &multisample,
                DepthStencilState = &depth,
                ColorBlendState = &blendState,
                DynamicState = &dynamic,
                Layout = _layout,
            }, null);
            Marshal.FreeHGlobal((nint)stages[0].Name);
            return pipeline;
        }
        finally
        {
            _vk.Device.DestroyShaderModule(fragment, null);
        }
    }

    public VkImageView HdrView(int slot) => _chains[slot].Hdr.View;

    public VkImage HdrImage(int slot) => _chains[slot].Hdr.Handle;

    /// <summary>Recrée les images à la taille de la cible. Le GPU doit être inactif (le rendu attend avant).</summary>
    public void Resize(int2 size)
    {
        foreach (var c in _chains)
        {
            Destroy(c.Hdr);
            Create(c.Hdr, size, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit);
            var s = size;
            for (int i = 0; i < Levels; i++)
            {
                s = new int2(Math.Max(s.X / 2, 1), Math.Max(s.Y / 2, 1));
                Destroy(c.Bloom[i]);
                Create(c.Bloom[i], s, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit);
            }
            Write(c.HdrSet, c.Hdr, c.Hdr);
            for (int i = 0; i < Levels; i++)
                Write(c.BloomSets[i], c.Bloom[i], c.Bloom[i]);
            Write(c.CompositeSet, c.Hdr, c.Bloom[0]);
        }
    }

    void Write(VkDescriptorSet set, Image a, Image b)
    {
        var infos = stackalloc VkDescriptorImageInfo[2];
        infos[0] = new VkDescriptorImageInfo { Sampler = _sampler, ImageView = a.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        infos[1] = new VkDescriptorImageInfo { Sampler = _sampler, ImageView = b.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        _vk.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &infos[0] },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &infos[1] },
        ], []);
    }

    void Create(Image image, int2 size, VkImageUsageFlags usage)
    {
        _vk.Allocator.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = HdrFormat,
            Extent = new VkExtent3D(size.X, size.Y, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = usage,
            InitialLayout = VkImageLayout.Undefined,
        }, new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto }, out image.Handle, out image.Allocation, out _);
        image.View = _vk.Device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = image.Handle,
            ViewType = VkImageViewType._2D,
            Format = HdrFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);
        image.Size = size;
        // Lisible tout de suite : la composition échantillonne le premier niveau même sans bloom.
        var handle = image.Handle;
        _vk.Immediate(cmd => Swapchain.ImageBarrier(cmd, handle, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.None, VkAccessFlags.ShaderReadBit));
        image.Layout = VkImageLayout.ShaderReadOnlyOptimal;
    }

    void Destroy(Image image)
    {
        if (image.Handle.IsNull())
            return;
        _vk.Device.DestroyImageView(image.View, null);
        _vk.Allocator.DestroyImage(image.Handle, image.Allocation);
        image.Handle = default;
    }

    static void ToAttachment(CommandBuffer cmd, Image image)
    {
        if (image.Layout == VkImageLayout.ColorAttachmentOptimal)
            return;
        Swapchain.ImageBarrier(cmd, image.Handle, image.Layout, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.ColorAttachmentReadBit);
        image.Layout = VkImageLayout.ColorAttachmentOptimal;
    }

    static void ToRead(CommandBuffer cmd, Image image)
    {
        if (image.Layout == VkImageLayout.ShaderReadOnlyOptimal)
            return;
        Swapchain.ImageBarrier(cmd, image.Handle, image.Layout, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
        image.Layout = VkImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>Prépare la cible HDR d'une image pour le rendu de la scène.</summary>
    public void BeginScene(CommandBuffer cmd, int slot) => ToAttachment(cmd, _chains[slot].Hdr);

    void Pass(CommandBuffer cmd, VkPipeline pipeline, VkDescriptorSet set, Image? target, VkImageView view, int2 size, float4 parameters, bool load)
    {
        var attachment = new VkRenderingAttachmentInfo
        {
            ImageView = view,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = load ? VkAttachmentLoadOp.Load : VkAttachmentLoadOp.DontCare,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var extent = new VkExtent2D(size.X, size.Y);
        cmd.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(extent), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &attachment });
        cmd.SetViewport(0, [new VkViewport { Width = size.X, Height = size.Y, MaxDepth = 1 }]);
        cmd.SetScissor(0, [new VkRect2D(extent)]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [set], []);
        var push = new PostPush { Params = parameters };
        cmd.PushConstants(_layout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&push, sizeof(PostPush)));
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRendering();
    }

    /// <summary>
    /// Bloom et tonemapping : de la cible HDR de l'image <paramref name="slot"/> vers la cible 8 bits
    /// (<paramref name="ldrView"/>, déjà en ColorAttachmentOptimal).
    /// </summary>
    public void Apply(CommandBuffer cmd, int slot, VkImageView ldrView, int2 size, PostSettings settings)
    {
        var c = _chains[slot];
        ToRead(cmd, c.Hdr);
        bool bloom = settings.Bloom > 0.001f;
        if (bloom)
        {
            // Réductions : HDR → niveau 0 (avec seuil doux) → niveau 1 → …
            for (int i = 0; i < Levels; i++)
            {
                var src = i == 0 ? c.Hdr : c.Bloom[i - 1];
                var set = i == 0 ? c.HdrSet : c.BloomSets[i - 1];
                ToAttachment(cmd, c.Bloom[i]);
                Pass(cmd, i == 0 ? _downFirst : _down, set, c.Bloom[i], c.Bloom[i].View, c.Bloom[i].Size,
                    new float4(1f / src.Size.X, 1f / src.Size.Y, settings.Threshold, 0), load: false);
                ToRead(cmd, c.Bloom[i]);
            }
            // Agrandissements : chaque niveau s'ajoute au niveau plus grand.
            for (int i = Levels - 1; i > 0; i--)
            {
                ToAttachment(cmd, c.Bloom[i - 1]);
                Pass(cmd, _up, c.BloomSets[i], c.Bloom[i - 1], c.Bloom[i - 1].View, c.Bloom[i - 1].Size,
                    new float4(1f / c.Bloom[i].Size.X, 1f / c.Bloom[i].Size.Y, 1, 0), load: true);
                ToRead(cmd, c.Bloom[i - 1]);
            }
        }
        Pass(cmd, _composite, c.CompositeSet, null, ldrView, size, new float4(0, 0, bloom ? settings.Bloom : 0, settings.Exposure), load: false);
    }

    public void Dispose()
    {
        foreach (var c in _chains)
        {
            Destroy(c.Hdr);
            foreach (var b in c.Bloom)
                Destroy(b);
        }
        foreach (var p in new[] { _down, _downFirst, _up, _composite })
            _vk.Device.DestroyPipeline(p, null);
        _vk.Device.DestroyPipelineLayout(_layout, null);
        _vk.Device.DestroyDescriptorPool(_pool, null);
        _vk.Device.DestroyDescriptorSetLayout(_setLayout, null);
        _vk.Device.DestroySampler(_sampler, null);
    }
}
