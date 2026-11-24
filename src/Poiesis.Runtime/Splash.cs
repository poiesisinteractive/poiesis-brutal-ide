using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Poiesis.Render.Vulkan;

namespace Poiesis.Runtime;

/// <summary>
/// Le logo BRUTAL, affiché au démarrage avant que l'éditeur ne soit utilisable (BRUTAL License v1.0, section 2.2 :
/// non modifié sauf mise à l'échelle, lisible, aussi visible que les autres logos), et dans la fenêtre « À propos ».
/// </summary>
public sealed class Splash : IDisposable
{
    readonly Texture? _logo;
    readonly VkSampler _sampler;
    readonly VulkanContext _vk;
    readonly ImTextureRef _texture;
    readonly double _seconds;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public Splash(VulkanContext vk, VulkanBackend backend, double seconds)
    {
        _vk = vk;
        _seconds = seconds;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "LOGO_BRUTAL_WHT.png");
        _sampler = vk.Device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
        }, null);
        if (File.Exists(path))
        {
            _logo = Texture.LoadPng(vk, path);
            _texture = backend.AddTexture(_sampler, _logo.View, VkImageLayout.ShaderReadOnlyOptimal);
        }
    }

    public bool Active => _clock.Elapsed.TotalSeconds < _seconds;

    /// <summary>Dessine le splash plein écran tant qu'il est actif ; rend vrai si l'éditeur doit attendre.</summary>
    public bool Draw()
    {
        if (!Active)
            return false;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new float4(0, 0, 0, 1));
        ImGui.Begin("##splash"u8, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoInputs);
        if (_logo is not null)
        {
            float width = MathF.Min(vp.Size.X * 0.5f, 712);
            var size = new float2(width, width * _logo.Size.Y / _logo.Size.X);
            ImGui.SetCursorPos((vp.Size - size) * 0.5f);
            ImGui.Image(_texture, size);
            var caption = "Construit avec BRUTAL";
            ImGui.SetCursorPosX((vp.Size.X - ImGui.CalcTextSize(caption).X) * 0.5f);
            ImGui.TextDisabled(caption);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        return true;
    }

    /// <summary>Le logo à la largeur donnée (fenêtre « À propos »).</summary>
    public void DrawLogo(float width)
    {
        if (_logo is null)
            return;
        // Fond noir : le logo blanc doit rester lisible quel que soit le thème.
        var pos = ImGui.GetCursorScreenPos();
        var size = new float2(width, width * _logo.Size.Y / _logo.Size.X);
        ImGui.GetWindowDrawList().AddRectFilled(pos - new float2(8, 8), pos + size + new float2(8, 8), EditorTheme.Col(new float4(0, 0, 0, 1)));
        ImGui.Image(_texture, size);
    }

    public void Dispose()
    {
        _logo?.Dispose();
        _vk.Device.DestroySampler(_sampler, null);
    }
}
