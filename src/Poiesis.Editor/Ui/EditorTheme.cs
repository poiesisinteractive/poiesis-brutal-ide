using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Poiesis.Editor.Ui;

/// <summary>Thème sombre et sobre, accent violet (la couleur de l'Orionum).</summary>
public static class EditorTheme
{
    public static readonly float4 Accent = new(0.56f, 0.38f, 0.96f, 1f);
    public static readonly float4 Coalition = new(0.45f, 0.70f, 1.00f, 1f);
    public static readonly float4 Clans = new(1.00f, 0.55f, 0.20f, 1f);
    public static readonly float4 Warning = new(1.00f, 0.78f, 0.30f, 1f);
    public static readonly float4 Error = new(1.00f, 0.40f, 0.40f, 1f);
    public static readonly float4 Muted = new(0.60f, 0.60f, 0.65f, 1f);

    public static void Apply()
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 2;
        style.FrameRounding = 2;
        style.TabRounding = 2;
        style.FramePadding = new float2(6, 4);
        style.ItemSpacing = new float2(8, 5);
        style.WindowBorderSize = 1;

        ref var c = ref style.Colors;
        float4 bg = new(0.10f, 0.10f, 0.11f, 1f);
        float4 panel = new(0.13f, 0.13f, 0.15f, 1f);
        float4 frame = new(0.18f, 0.18f, 0.21f, 1f);
        float4 accentDim = new(0.38f, 0.26f, 0.66f, 1f);

        c[(int)ImGuiCol.WindowBg] = panel;
        c[(int)ImGuiCol.ChildBg] = panel;
        c[(int)ImGuiCol.PopupBg] = bg;
        c[(int)ImGuiCol.DockingEmptyBg] = bg;
        c[(int)ImGuiCol.MenuBarBg] = bg;
        c[(int)ImGuiCol.TitleBg] = bg;
        c[(int)ImGuiCol.TitleBgActive] = bg;
        c[(int)ImGuiCol.FrameBg] = frame;
        c[(int)ImGuiCol.FrameBgHovered] = new float4(0.24f, 0.24f, 0.28f, 1f);
        c[(int)ImGuiCol.FrameBgActive] = new float4(0.28f, 0.28f, 0.33f, 1f);
        c[(int)ImGuiCol.Header] = accentDim;
        c[(int)ImGuiCol.HeaderHovered] = Accent;
        c[(int)ImGuiCol.HeaderActive] = Accent;
        c[(int)ImGuiCol.Button] = frame;
        c[(int)ImGuiCol.ButtonHovered] = accentDim;
        c[(int)ImGuiCol.ButtonActive] = Accent;
        c[(int)ImGuiCol.Tab] = bg;
        c[(int)ImGuiCol.TabHovered] = accentDim;
        c[(int)ImGuiCol.TabSelected] = panel;
        c[(int)ImGuiCol.TabDimmed] = bg;
        c[(int)ImGuiCol.TabDimmedSelected] = panel;
        c[(int)ImGuiCol.TabSelectedOverline] = Accent;
        c[(int)ImGuiCol.CheckMark] = Accent;
        c[(int)ImGuiCol.SliderGrab] = Accent;
        c[(int)ImGuiCol.SliderGrabActive] = Accent;
        c[(int)ImGuiCol.DockingPreview] = Accent with { W = 0.5f };
        c[(int)ImGuiCol.SeparatorHovered] = Accent;
        c[(int)ImGuiCol.ResizeGripHovered] = Accent;
    }
}
