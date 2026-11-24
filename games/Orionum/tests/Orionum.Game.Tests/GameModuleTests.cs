using Poiesis.Editor;
using Poiesis.Engine.Hosting;
using Poiesis.Engine.Scene;

namespace Orionum.Game.Tests;

/// <summary>
/// Tests de bout en bout, comme l'éditeur et le runner headless : la DLL du jeu est chargée dans un contexte
/// rechargeable, la scène JSON est lue, cuite et jouée.
/// </summary>
public class GameModuleTests
{
    internal static readonly string GameRoot = FindGameRoot();
    internal static string ContentRoot => Path.Combine(GameRoot, "content");
    internal static string Scene(string name) => Path.Combine(ContentRoot, "scenarios", name + ".scene.json");

    internal static string GameDll()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(GameRoot, "src", "Orionum.Game", "bin", config, "net10.0", "Orionum.Game.dll");
    }

    static string FindGameRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content", "ships")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("games/Orionum introuvable");
    }

    [Fact]
    public void Capital_battle_scene_loads_from_the_dll_and_bakes_both_fleets_and_bodies()
    {
        var registry = new NodeTypeRegistry();
        using var host = new GameModuleHost(registry, ContentRoot);
        var module = host.Load(GameDll());
        var scene = new SceneSerializer(registry).Read(File.ReadAllText(Scene("capital-battle")));

        var session = new PlaySession(scene, module);
        Assert.Equal(25 * 2 + 2, session.World.EntityCount);
        session.Run(20 * 30);
        Assert.Equal(7, session.Schedule.Timings.Count);
    }

    [Fact]
    public void Hot_reload_keeps_the_edited_scene_and_unloads_the_old_assembly()
    {
        using var editor = new EditorSession(GameRoot);
        var fleetId = EditAndPlayBeforeReload(editor);

        bool unloaded = editor.HotReload(GameDll());

        Assert.True(unloaded, "l'ancienne assembly du jeu est encore retenue");
        Assert.Equal(2, editor.Modules.Generation);
        Assert.Equal("Coalition (modifiée)", editor.Scene.Children[0].Name);
        Assert.Equal(fleetId, editor.Selection?.Id);
        Assert.True(editor.IsDirty, "le renommage non enregistré reste à enregistrer");

        editor.SaveScene(Path.Combine(Path.GetTempPath(), $"poiesis-{Guid.NewGuid():N}.scene.json"));
        editor.HotReload(GameDll());
        Assert.False(editor.IsDirty, "recharger le code ne modifie pas le document");

        // La nouvelle génération du module joue : 0,25 s à 20 Hz = 5 pas.
        editor.StartPlay();
        editor.Update(0.25);
        Assert.Equal(5, editor.Play!.World.Tick);
    }

    /// <summary>
    /// Charge, édite et joue avec la première génération du module. Méthode séparée et non inlinée :
    /// aucune variable locale du test ne doit garder un objet de l'ancienne assembly.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static Guid EditAndPlayBeforeReload(EditorSession editor)
    {
        editor.LoadGameModule(GameDll());
        editor.OpenScene(Scene("capital-battle"));
        var fleet = editor.Scene.Children[0];
        editor.History.Execute(new RenameCommand(fleet, "Coalition (modifiée)"));
        editor.Selection = fleet;
        editor.StartPlay();
        editor.Update(0.5);
        editor.Stop();
        return fleet.Id;
    }
}
