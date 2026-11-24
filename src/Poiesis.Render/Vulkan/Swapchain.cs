using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Poiesis.Render.Vulkan;

/// <summary>Une image en vol : ses commandes et sa synchronisation.</summary>
public sealed class FrameContext
{
    internal VkCommandPool Pool;
    internal VkFence Fence;
    internal VkSemaphore ImageAvailable;

    public CommandBuffer Commands { get; internal set; }

    /// <summary>Indice de l'image en vol (0 ou 1), pour les ressources dupliquées par image.</summary>
    public int Slot { get; internal set; }
}

/// <summary>
/// La swapchain de la fenêtre et la passe de rendu classique sur laquelle ImGui dessine
/// (son backend Vulkan veut une <see cref="VkRenderPass"/>). Deux images en vol, recréation au redimensionnement,
/// et capture de l'image présentée en PNG (mode <c>--capture</c> de l'éditeur).
/// </summary>
public sealed unsafe class Swapchain : IDisposable
{
    public const int FramesInFlight = 2;

    readonly VulkanContext _vk;
    readonly GlfwWindow _window;
    readonly FrameContext[] _frames = new FrameContext[FramesInFlight];
    VkSwapchainKHR _swapchain;
    VkImage[] _images = [];
    VkImageView[] _views = [];
    VkFramebuffer[] _framebuffers = [];
    VkSemaphore[] _renderFinished = [];
    int _frameIndex;
    int _imageIndex;
    bool _needsRebuild;
    string? _pendingCapture;

    public Swapchain(VulkanContext vk, GlfwWindow window)
    {
        _vk = vk;
        _window = window;

        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[vk.PhysicalDevice.GetSurfaceFormatsKHR_Count(vk.Surface)];
        vk.PhysicalDevice.GetSurfaceFormatsKHR_Write(vk.Surface, formats);
        // On préfère un format UNORM : ImGui écrit des couleurs déjà en sRGB.
        SurfaceFormat = formats[0];
        foreach (var f in formats)
            if (f.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm)
            {
                SurfaceFormat = f;
                break;
            }

        CreateRenderPass();
        for (int i = 0; i < FramesInFlight; i++)
        {
            var pool = vk.Device.CreateCommandPool(new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = vk.QueueFamily,
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            }, null);
            _frames[i] = new FrameContext
            {
                Pool = pool,
                Commands = vk.Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }),
                Fence = vk.Device.CreateFence(VkFenceCreateFlags.SignaledBit, null),
                ImageAvailable = vk.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null),
                Slot = i,
            };
        }
        Build();
    }

    public VkSurfaceFormatKHR SurfaceFormat { get; }

    public VkRenderPass RenderPass { get; private set; }

    public VkExtent2D Extent { get; private set; }

    public int ImageCount => _images.Length;

    public int MinImageCount { get; private set; }

    public float4 ClearColor { get; set; } = new(0.08f, 0.08f, 0.09f, 1f);

    /// <summary>Demande une capture PNG de la prochaine image présentée.</summary>
    public void RequestCapture(string path) => _pendingCapture = path;

    /// <summary>Signale un redimensionnement (la swapchain est reconstruite à la prochaine image).</summary>
    public void Invalidate() => _needsRebuild = true;

    /// <summary>
    /// Commence une image : attend sa barrière, acquiert une image de la swapchain, ouvre le command buffer.
    /// Rend <c>null</c> si la fenêtre est réduite (rien à dessiner).
    /// </summary>
    public FrameContext? BeginFrame()
    {
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0)
            return null;
        if (_needsRebuild || size.X != Extent.Width || size.Y != Extent.Height)
            Rebuild();

        var frame = _frames[_frameIndex];
        _vk.Device.WaitForFence(frame.Fence, -1);

        var result = _vk.Device.AcquireNextImageKHR(_swapchain, -1, frame.ImageAvailable, VkFence.NullHandle, out _imageIndex);
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            Rebuild();
            return null;
        }
        if (result == VkResult.SuboptimalKHR)
            _needsRebuild = true;

        _vk.Device.ResetFences([frame.Fence]);
        frame.Commands.Reset();
        frame.Commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        return frame;
    }

    /// <summary>Ouvre la passe de rendu de la fenêtre (effacement), pour ImGui.</summary>
    public void BeginRenderPass(FrameContext frame)
    {
        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = ClearColor } };
        frame.Commands.BeginRenderPass(new VkRenderPassBeginInfo
        {
            RenderPass = RenderPass,
            Framebuffer = _framebuffers[_imageIndex],
            RenderArea = new VkRect2D(Extent),
            ClearValueCount = 1,
            ClearValues = &clear,
        }, VkSubpassContents.Inline);
    }

    /// <summary>Ferme la passe, envoie les commandes et présente. Fait la capture demandée, le cas échéant.</summary>
    public void EndFrame(FrameContext frame)
    {
        var cmd = frame.Commands;
        cmd.EndRenderPass();

        // Capture : l'image passe de PresentSrc à TransferSrc, est copiée dans un tampon lisible, puis revient.
        VkBuffer captureBuffer = default;
        VmaAllocation captureAllocation = default;
        VmaAllocationInfo captureInfo = default;
        var capturePath = _pendingCapture;
        if (capturePath is not null)
        {
            var bytes = (ulong)Extent.Width * (ulong)Extent.Height * 4;
            _vk.Allocator.CreateBuffer(
                new VkBufferCreateInfo { Size = new ByteSize64(bytes), Usage = VkBufferUsageFlags.TransferDstBit },
                new VmaAllocationCreateInfo
                {
                    Usage = VmaMemoryUsage.Auto,
                    Flags = VmaAllocationCreateFlags.HostAccessRandomBit | VmaAllocationCreateFlags.MappedBit,
                },
                out captureBuffer, out captureAllocation, out captureInfo);

            var image = _images[_imageIndex];
            ImageBarrier(cmd, image, VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal,
                VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit,
                VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit);
            cmd.CopyImageToBuffer(image, VkImageLayout.TransferSrcOptimal, captureBuffer,
            [
                new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D(Extent.Width, Extent.Height, 1),
                },
            ]);
            ImageBarrier(cmd, image, VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR,
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.BottomOfPipeBit,
                VkAccessFlags.TransferReadBit, VkAccessFlags.None);
        }

        cmd.End();
        var renderFinished = _renderFinished[_imageIndex];
        _vk.Queue.Submit([frame.ImageAvailable], [VkPipelineStageFlags.ColorAttachmentOutputBit], [cmd], [renderFinished], frame.Fence);

        var swapchain = _swapchain;
        var imageIndex = _imageIndex;
        var present = _vk.Queue.PresentKHR(new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &imageIndex,
        });
        if (present is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            _needsRebuild = true;

        if (capturePath is not null)
        {
            _vk.Device.WaitForFence(frame.Fence, -1);
            SavePng(capturePath, (byte*)captureInfo.PMappedData);
            _vk.Allocator.DestroyBuffer(captureBuffer, captureAllocation);
            _pendingCapture = null;
        }

        _frameIndex = (_frameIndex + 1) % FramesInFlight;
    }

    void SavePng(string path, byte* data)
    {
        int w = Extent.Width, h = Extent.Height;
        var pixels = new byte[w * h * 4];
        new ReadOnlySpan<byte>(data, pixels.Length).CopyTo(pixels);
        bool bgra = SurfaceFormat.Format is VkFormat.B8G8R8A8UNorm or VkFormat.B8G8R8A8SRGB;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (bgra)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Stb.WritePng(path, w, h, 4, pixels);
    }

    internal static void ImageBarrier(CommandBuffer cmd, VkImage image, VkImageLayout from, VkImageLayout to,
        VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage, VkAccessFlags srcAccess, VkAccessFlags dstAccess,
        VkImageAspectFlags aspect = VkImageAspectFlags.ColorBit)
    {
        cmd.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [], [],
        [
            new VkImageMemoryBarrier
            {
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            },
        ]);
    }

    void CreateRenderPass()
    {
        var attachment = new VkAttachmentDescription
        {
            Format = SurfaceFormat.Format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };
        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
        };
        // Le viewport hors écran est écrit avant la passe et lu par ImGui dans la passe.
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = VkAccessFlags.None,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.ShaderReadBit,
        };
        RenderPass = _vk.Device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = 1,
            Attachments = &attachment,
            SubpassCount = 1,
            Subpasses = &subpass,
            DependencyCount = 1,
            Dependencies = &dependency,
        }, null);
    }

    void Build()
    {
        var caps = _vk.PhysicalDevice.GetSurfaceCapabilitiesKHR(_vk.Surface);
        var extent = caps.CurrentExtent;
        if (extent.Width == -1 && extent.Height == -1)
        {
            var fb = _window.FramebufferSize;
            extent = new VkExtent2D(
                Math.Clamp(fb.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp(fb.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }
        Extent = extent;

        MinImageCount = Math.Max(caps.MinImageCount, 2);
        var imageCount = MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, caps.MaxImageCount);

        VkCompositeAlphaFlagsKHR[] alphaModes =
        [
            VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.InheritBitKHR,
        ];

        var old = _swapchain;
        _swapchain = _vk.Device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
        {
            Surface = _vk.Surface,
            MinImageCount = imageCount,
            ImageFormat = SurfaceFormat.Format,
            ImageColorSpace = SurfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = alphaModes.First(m => (caps.SupportedCompositeAlpha & m) != 0),
            PresentMode = VkPresentModeKHR.FifoKHR,
            Clipped = true,
            OldSwapchain = old,
        }, null);
        if (!old.IsNull())
            _vk.Device.DestroySwapchainKHR(old, null);

        _images = new VkImage[_vk.Device.GetSwapchainImagesKHR_Count(_swapchain)];
        _vk.Device.GetSwapchainImagesKHR_Write(_swapchain, _images);
        _views = new VkImageView[_images.Length];
        _framebuffers = new VkFramebuffer[_images.Length];
        _renderFinished = new VkSemaphore[_images.Length];
        for (int i = 0; i < _images.Length; i++)
        {
            _views[i] = _vk.Device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = _images[i],
                ViewType = VkImageViewType._2D,
                Format = SurfaceFormat.Format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            }, null);
            var view = _views[i];
            _framebuffers[i] = _vk.Device.CreateFramebuffer(new VkFramebufferCreateInfo
            {
                RenderPass = RenderPass,
                AttachmentCount = 1,
                Attachments = &view,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            }, null);
            _renderFinished[i] = _vk.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
        _needsRebuild = false;
    }

    void DestroyImages()
    {
        for (int i = 0; i < _images.Length; i++)
        {
            _vk.Device.DestroyFramebuffer(_framebuffers[i], null);
            _vk.Device.DestroyImageView(_views[i], null);
            _vk.Device.DestroySemaphore(_renderFinished[i], null);
        }
    }

    void Rebuild()
    {
        _vk.Device.WaitIdle();
        DestroyImages();
        Build();
    }

    public void Dispose()
    {
        _vk.Device.WaitIdle();
        DestroyImages();
        _vk.Device.DestroySwapchainKHR(_swapchain, null);
        foreach (var f in _frames)
        {
            _vk.Device.DestroyFence(f.Fence, null);
            _vk.Device.DestroySemaphore(f.ImageAvailable, null);
            _vk.Device.DestroyCommandPool(f.Pool, null);
        }
        _vk.Device.DestroyRenderPass(RenderPass, null);
    }
}
