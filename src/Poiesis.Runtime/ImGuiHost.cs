using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.ImGuiApi.FreeType;
using Brutal.VulkanApi;
using Poiesis.Render.Vulkan;

namespace Poiesis.Runtime;

/// <summary>
/// Dear ImGui sur ses backends GLFW et Vulkan (ceux de BRUTAL), branche docking.
/// La disposition des panneaux est gardée dans <c>.poiesis/imgui.ini</c> du projet.
/// </summary>
public sealed class ImGuiHost : IDisposable
{
    readonly ImGuiContextPtr _context;
    readonly GlfwBackend _platform;
    readonly VulkanBackend _renderer;
    readonly string _iniPath;

    public ImGuiHost(GlfwWindow window, VulkanContext vk, Swapchain swapchain, string projectRoot)
    {
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;

        _iniPath = Path.Combine(projectRoot, ".poiesis", "imgui.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(_iniPath)!);
        io.IniFilename = null;   // on gère le fichier nous-mêmes (chemin du projet)
        if (File.Exists(_iniPath))
            ImGui.LoadIniSettingsFromDisk(_iniPath);

        // FreeType et l'atlas dynamique d'ImGui 1.92 : tous les glyphes (accents compris) sont rastérisés à la demande.
        io.Fonts.SetFontLoader(ImGuiFreeType.GetFontLoader());
        if (FindUiFont() is { } font)
            io.Fonts.AddFontFromFileTTF(font, 16f);
        EditorTheme.Apply();

        _platform = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);
        _renderer = new VulkanBackend(new VulkanBackend.CreateInfo
        {
            Device = vk.Device,
            GraphicsQueue = vk.Queue,
            RenderPass = swapchain.RenderPass,
            MinImageCount = swapchain.MinImageCount,
            ImageCount = Math.Max(swapchain.ImageCount, swapchain.MinImageCount),
            SampleCount = VkSampleCountFlags._1Bit,
            DescriptorPoolSize = 64,
        });
    }

    public VulkanBackend Renderer => _renderer;

    public bool HasLayout => File.Exists(_iniPath);

    public void NewFrame()
    {
        _platform.NewFrame();
        _renderer.NewFrame();
        ImGui.NewFrame();
        ImString.SharedStorage.Reset();
    }

    public void Render(FrameContext frame)
    {
        ImGui.Render();
        _renderer.RenderDrawData(ImGui.GetDrawData(), frame.Commands);
    }

    public void SaveLayout() => ImGui.SaveIniSettingsToDisk(_iniPath);

    /// <summary>Une police d'interface du système : Segoe UI sous Windows, sinon une police libre courante.</summary>
    static string? FindUiFont()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/truetype/crosextra/Carlito-Regular.ttf",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        SaveLayout();
        _renderer.Dispose();
        _platform.Dispose();
        ImGui.DestroyContext(_context);
    }
}
