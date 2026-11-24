using Brutal.GlfwApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Poiesis.Render.Vulkan;

/// <summary>
/// Les objets Vulkan de base : instance, surface de la fenêtre, device, file graphique (qui présente aussi),
/// allocateur VMA et un pool de commandes pour les envois ponctuels (téléversements, captures).
/// Repris du <c>DemoVulkan</c> de BRUTAL. Vulkan 1.3 : rendu dynamique pour le viewport.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    public VulkanContext(GlfwWindow window)
    {
        // GLFW ne trouve pas seul le chargeur Vulkan de BRUTAL : y toucher d'abord le charge.
        _ = Brutal.VulkanApi.Vulkan.GetInstanceProcAddr;

        var instanceExtensions = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(instanceExtensions);
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var glfwExtensionCount);
        for (var i = 0; i < glfwExtensionCount; i++)
            instanceExtensions.Add(Utf8.GetString(glfwExtensions[i]));

        Instance = Brutal.VulkanApi.Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3 },
            Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
            EnabledExtensions = instanceExtensions,
            EnabledLayers = [],
        });

        if (Glfw.CreateWindowSurface(Instance.VkHandle, window, out var surfaceHandle) != (int)VkResult.Success)
            throw new InvalidOperationException("Impossible de créer la surface Vulkan de la fenêtre");
        Surface = new VkSurfaceKHR((nint)surfaceHandle);

        // Une seule file qui dessine et présente. On préfère un GPU dédié, puis intégré, puis logiciel (lavapipe).
        PhysicalDevice? best = null;
        int bestScore = -1;
        foreach (var candidate in Instance.PhysicalDevices)
        {
            var families = new VkQueueFamilyProperties[candidate.GetQueueFamilyProperties_Count()];
            candidate.GetQueueFamilyProperties_Write(families);
            for (var i = 0; i < families.Length; i++)
            {
                if ((families[i].QueueFlags & VkQueueFlags.GraphicsBit) == 0 || !candidate.GetSurfaceSupportKHR(i, Surface))
                    continue;
                var score = candidate.GetProperties().DeviceType switch
                {
                    VkPhysicalDeviceType.DiscreteGpu => 3,
                    VkPhysicalDeviceType.IntegratedGpu => 2,
                    VkPhysicalDeviceType.VirtualGpu => 1,
                    _ => 0,
                };
                if (score > bestScore)
                {
                    (best, bestScore, QueueFamily) = (candidate, score, i);
                }
                break;
            }
        }
        PhysicalDevice = best ?? throw new InvalidOperationException("Aucun GPU Vulkan ne peut dessiner et présenter dans la fenêtre");
        var props = PhysicalDevice.GetProperties();
        DeviceName = Utf8.GetString(props.DeviceName);

        var deviceExtensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(deviceExtensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, PhysicalDevice);

        var priority = 1.0f;
        var vulkan13Features = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
        Device = PhysicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = deviceExtensions,
            QueueCreateInfos =
            [
                new VkDeviceQueueCreateInfo { QueueFamilyIndex = QueueFamily, QueueCount = 1, QueuePriorities = &priority },
            ],
            Next = &vulkan13Features,
        });
        Queue = Device.GetQueue(QueueFamily, 0);

        Allocator = Device.CreateVmaAllocator(new VmaAllocatorCreateInfo
        {
            Instance = Instance,
            PhysicalDevice = PhysicalDevice,
            Device = Device,
            VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3,
        });

        UploadPool = Device.CreateCommandPool(new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = QueueFamily,
            Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
        }, null);
    }

    public Instance Instance { get; }
    public VkSurfaceKHR Surface { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public string DeviceName { get; }
    public int QueueFamily { get; private set; }
    public DeviceEx Device { get; }
    public Queue Queue { get; }
    public VmaAllocator Allocator { get; }
    public VkCommandPool UploadPool { get; }

    /// <summary>Enregistre et exécute des commandes, puis attend leur fin (téléversements, captures).</summary>
    public void Immediate(Action<CommandBuffer> record)
    {
        var cmd = Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = UploadPool, Level = VkCommandBufferLevel.Primary });
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        record(cmd);
        cmd.End();
        var fence = Device.CreateFence(VkFenceCreateFlags.None, null);
        Queue.Submit([], [], [cmd], [], fence);
        Device.WaitForFence(fence, -1);
        Device.DestroyFence(fence, null);
        Device.FreeCommandBuffers(UploadPool, [cmd]);
    }

    public void Dispose()
    {
        Device.WaitIdle();
        Device.DestroyCommandPool(UploadPool, null);
        Device.DestroyVmaAllocator(Allocator);
        Device.Dispose();
        Instance.DestroySurfaceKHR(Surface, null);
        Instance.Dispose();
    }
}
