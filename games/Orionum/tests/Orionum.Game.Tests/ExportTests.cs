using Poiesis.Editor;

namespace Orionum.Game.Tests;

/// <summary>Export autonome du jeu (lent : publication complète du joueur, environ une minute).</summary>
public class ExportTests
{
    [Theory]
    [InlineData(ExportTarget.LinuxX64, "Orionum")]
    [InlineData(ExportTarget.WindowsX64, "Orionum.exe")]
    public async Task Export_produces_a_self_contained_game_with_its_dll_content_and_brutal_notice(ExportTarget target, string executable)
    {
        var project = ProjectConfig.Load(GameModuleTests.GameRoot);
        var log = new LogBuffer();
        var result = await new Exporter(project, log).ExportAsync(target, default);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(result.Folder, executable)), $"{executable} absent");
        Assert.True(File.Exists(Path.Combine(result.Folder, "game", "Orionum.Game.dll")));
        Assert.False(File.Exists(Path.Combine(result.Folder, "game", "Poiesis.Engine.Core.dll")), "le moteur vient du joueur, pas du jeu");
        Assert.True(File.Exists(Path.Combine(result.Folder, "content", "ships", "clans.flagship.ship.json")));
        Assert.True(File.Exists(Path.Combine(result.Folder, "NOTICE.txt")), "NOTICE.txt de BRUTAL (licence, section 2.3)");
        Assert.True(File.Exists(Path.Combine(result.Folder, "Assets", "LOGO_BRUTAL_WHT.png")), "logo BRUTAL (licence, section 2.2)");
        Assert.Contains("capital-battle", File.ReadAllText(Path.Combine(result.Folder, "game.json")));
        Assert.True(File.Exists(result.Archive));
    }
}
