using Poiesis.Editor;

namespace Poiesis.AI.Tests;

/// <summary>Lecture des sorties de dotnet build et dotnet test, et lancement de l'éditeur de code.</summary>
public class ToolingTests
{
    const string Root = "/repo/games/Orionum";

    [Fact]
    public void Build_errors_are_parsed_once_with_their_location()
    {
        var output = """
            /repo/games/Orionum/src/Orionum.Game/Visuals.cs(23,35): error CS0029: Cannot implicitly convert type 'string' to 'int' [/repo/x.csproj]
            /repo/games/Orionum/src/Orionum.Game/Visuals.cs(23,35): error CS0029: Cannot implicitly convert type 'string' to 'int' [/repo/x.csproj]
            /repo/games/Orionum/src/Orionum.Game/Nodes.cs(4,1): warning CS0105: Using directive appeared previously [/repo/x.csproj]
            """;
        var diagnostics = BuildService.Parse(output);
        Assert.Equal(2, diagnostics.Count);
        Assert.True(diagnostics[0].IsError);
        Assert.Equal((23, 35), (diagnostics[0].Line, diagnostics[0].Column));
        Assert.EndsWith("Visuals.cs", diagnostics[0].File);
        Assert.False(diagnostics[1].IsError);
    }

    [Theory]
    [InlineData("Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 1 s - Orionum.Game.Tests.dll (net10.0)")]
    [InlineData("Total tests: 3\n     Passed: 2\n     Failed: 1\n Total time: 1.4 Seconds")]
    public void Test_failures_are_counted_and_located_in_the_game_code(string summary)
    {
        var output = $"""
              Failed Orionum.Game.Tests.BattleTests.Hull_damage [7 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
              Stack Trace:
                 at Orionum.Game.Tests.BattleTests.Hull_damage() in {Root}/tests/Orionum.Game.Tests/BattleTests.cs:line 47
                 at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
            {summary}
            """;
        var run = TestService.Parse(false, output, TimeSpan.FromSeconds(1), Root);

        Assert.Equal(2, run.Passed);
        Assert.Equal(1, run.Failed);
        var failure = Assert.Single(run.Failures);
        Assert.Equal("Orionum.Game.Tests.BattleTests.Hull_damage", failure.Name);
        Assert.Equal("Assert.Equal() Failure: Values differ", failure.Message);
        Assert.Equal(47, failure.Location?.Line);
        Assert.EndsWith("BattleTests.cs", failure.Location?.File);
    }

    [Fact]
    public void The_code_editor_command_and_arguments_come_from_the_project_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "poiesis-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".poiesis"));
        File.WriteAllText(Path.Combine(root, "poiesis.project.json"), """{ "name": "T", "gameProject": "a.csproj", "gameAssembly": "a.dll" }""");
        File.WriteAllText(Path.Combine(root, ".poiesis", "settings.json"), """{ "codeEditor": { "command": "rider", "goto": "--line {line} {file}" } }""");

        var editor = CodeEditor.Load(ProjectConfig.Load(root), new LogBuffer());

        Assert.Equal("rider", editor.Command);
        Assert.Equal("--line {line} {file}", editor.GotoArgs);
        Assert.Equal("{folder}", editor.OpenArgs);
    }

    [Fact]
    public void The_game_assembly_is_stale_when_missing_or_older_than_a_source_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "poiesis-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "bin"));
        File.WriteAllText(Path.Combine(root, "poiesis.project.json"), """{ "name": "T", "gameProject": "src/a.csproj", "gameAssembly": "src/bin/a.dll" }""");
        var project = ProjectConfig.Load(root);
        var t0 = DateTime.UtcNow.AddHours(-1);
        void Write(string rel, DateTime at)
        {
            var path = Path.Combine(root, rel);
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, at);
        }
        Write("src/a.csproj", t0);
        Write("src/Nodes.cs", t0);
        Assert.True(project.IsGameAssemblyStale());   // pas de DLL

        Write("src/bin/a.dll", t0.AddMinutes(1));
        Write("src/bin/later.cs", t0.AddMinutes(5));   // bin/ ne compte pas
        Assert.False(project.IsGameAssemblyStale());

        Write("src/Nodes.cs", t0.AddMinutes(2));        // source modifiée après le build (un pull)
        Assert.True(project.IsGameAssemblyStale());
    }
}
