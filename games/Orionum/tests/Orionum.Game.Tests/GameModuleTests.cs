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
    static readonly string RepoGameRoot = FindGameRoot();
    static string ContentRoot => Path.Combine(RepoGameRoot, "content");
    static string ScenePath => Path.Combine(ContentRoot, "scenarios", "line-battle.scene.json");

    static string GameDll()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(RepoGameRoot, "src", "Orionum.Game", "bin", config, "net10.0", "Orionum.Game.dll");
    }

    static string FindGameRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content", "ships")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("games/Orionum introuvable");
    }

    [Fact]
    public void Line_battle_scene_loads_bakes_and_ends_with_a_winner()
    {
        var registry = new NodeTypeRegistry();
        using var host = new GameModuleHost(registry, ContentRoot);
        var module = host.Load(GameDll());
        var scene = new SceneSerializer(registry).Read(File.ReadAllText(ScenePath));

        var session = new PlaySession(scene, module);
        Assert.Equal(12, session.World.EntityCount);

        session.Run(20 * 600);   // 10 minutes de bataille
        Assert.InRange(session.World.EntityCount, 1, 11);
    }

    [Fact]
    public void Same_scene_gives_the_same_outcome()
    {
        int Run()
        {
            var registry = new NodeTypeRegistry();
            using var host = new GameModuleHost(registry, ContentRoot);
            var module = host.Load(GameDll());
            var session = new PlaySession(new SceneSerializer(registry).Read(File.ReadAllText(ScenePath)), module);
            session.Run(20 * 120);
            return session.World.EntityCount;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Hot_reload_keeps_the_edited_scene_and_unloads_the_old_assembly()
    {
        using var editor = new EditorSession(RepoGameRoot);
        var fleetId = EditAndPlayBeforeReload(editor);

        bool unloaded = editor.HotReload(GameDll());

        Assert.True(unloaded, "l'ancienne assembly du jeu est encore retenue");
        Assert.Equal(2, editor.Modules.Generation);
        Assert.Equal("Coalition (modifiée)", editor.Scene.Children[0].Name);
        Assert.Equal(fleetId, editor.Selection?.Id);

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
        editor.OpenScene(ScenePath);
        var fleet = editor.Scene.Children[0];
        editor.History.Execute(new RenameCommand(fleet, "Coalition (modifiée)"));
        editor.Selection = fleet;
        editor.StartPlay();
        editor.Update(0.5);
        editor.Stop();
        return fleet.Id;
    }
}
