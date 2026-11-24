using Brutal;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace Poiesis.Render.Vulkan;

/// <summary>Texture RGBA 8 bits en lecture seule (logo, icônes), téléversée une fois par un tampon intermédiaire.</summary>
public sealed unsafe class Texture : IDisposable
{
    readonly VulkanContext _vk;
    readonly VkImage _image;
    readonly VmaAllocation _allocation;

    public Texture(VulkanContext vk, int2 size, ReadOnlySpan<byte> rgba)
    {
        _vk = vk;
        Size = size;
        vk.Allocator.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = VkFormat.R8G8B8A8UNorm,
            Extent = new VkExtent3D(size.X, size.Y, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
            InitialLayout = VkImageLayout.Undefined,
        }, new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto }, out _image, out _allocation, out _);

        vk.Allocator.CreateBuffer(
            new VkBufferCreateInfo { Size = new ByteSize64((ulong)rgba.Length), Usage = VkBufferUsageFlags.TransferSrcBit },
            new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
            },
            out var staging, out var stagingAlloc, out var stagingInfo);
        rgba.CopyTo(new Span<byte>((void*)stagingInfo.PMappedData, rgba.Length));

        var image = _image;
        vk.Immediate(cmd =>
        {
            Swapchain.ImageBarrier(cmd, image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.None, VkAccessFlags.TransferWriteBit);
            cmd.CopyBufferToImage(staging, image, VkImageLayout.TransferDstOptimal,
            [
                new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D(size.X, size.Y, 1),
                },
            ]);
            Swapchain.ImageBarrier(cmd, image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
        });
        vk.Allocator.DestroyBuffer(staging, stagingAlloc);

        View = vk.Device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = _image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R8G8B8A8UNorm,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);
    }

    public int2 Size { get; }

    public VkImageView View { get; }

    public static Texture LoadPng(VulkanContext vk, string path)
    {
        using var image = Stb.Load(path, 4);
        return new Texture(vk, new int2(image.Width, image.Height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.AsSpan<byte4>()));
    }

    public void Dispose()
    {
        _vk.Device.DestroyImageView(View, null);
        _vk.Allocator.DestroyImage(_image, _allocation);
    }
}
