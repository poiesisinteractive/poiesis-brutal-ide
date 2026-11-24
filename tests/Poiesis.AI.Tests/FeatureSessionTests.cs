using System.Text.Json;
using Poiesis.AI.Agent;
using Poiesis.AI.Features;

namespace Poiesis.AI.Tests;

public class FeatureSessionTests : IDisposable
{
    readonly string _repo = Path.Combine(Path.GetTempPath(), "poiesis-feature-" + Guid.NewGuid().ToString("N"));

    public FeatureSessionTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "jeu\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".poiesis/\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
    }

    void Git(params string[] args)
    {
        var r = ProcessRunner.RunAsync("git", args, _repo, TimeSpan.FromSeconds(30), default).GetAwaiter().GetResult();
        Assert.True(r.Succeeded, r.Output);
    }

    static ChatResponse WriteThenFinish(string path, string content) => ScriptedProvider.Reply(StopReason.ToolUse,
        new ToolCallPart("w", "write_file", JsonSerializer.SerializeToElement(new { path, content })));

    static readonly WorkspaceCommand Pass = new("git", ["status", "--short"], TimeSpan.FromSeconds(30));
    static readonly WorkspaceCommand Fail = new("git", ["no-such-command"], TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Feature_is_built_in_a_branch_then_merged_on_accept()
    {
        var provider = new ScriptedProvider(
            WriteThenFinish("content/ships/new.ship.json", "{ \"id\": \"new\" }"),
            ScriptedProvider.Reply(StopReason.EndTurn, new TextPart("Ajouté.")));
        var session = new FeatureSession(provider, [new ValidationGate("build", Pass)], new WorkspaceCommands(Pass, Pass, _ => Pass));

        var proposal = await session.RunAsync(_repo, "Nouveau vaisseau", "Ajoute une classe.", "test");

        Assert.Equal(FeatureStatus.Ready, proposal.Status);
        Assert.Contains("content/ships/new.ship.json", proposal.Diff);
        Assert.False(File.Exists(Path.Combine(_repo, "content/ships/new.ship.json")), "le projet principal ne doit pas bouger avant acceptation");

        await proposal.Workspace.AcceptAsync();

        Assert.True(File.Exists(Path.Combine(_repo, "content/ships/new.ship.json")));
        Assert.False(Directory.Exists(proposal.Workspace.Path));
    }

    [Fact]
    public async Task Failing_gates_are_sent_back_to_the_agent_until_the_repair_budget_is_spent()
    {
        var replies = new List<ChatResponse> { WriteThenFinish("a.txt", "1"), ScriptedProvider.Reply(StopReason.EndTurn, new TextPart("fini")) };
        for (int i = 0; i < 2; i++)
            replies.Add(ScriptedProvider.Reply(StopReason.EndTurn, new TextPart("corrigé")));
        var provider = new ScriptedProvider(replies.ToArray());
        var session = new FeatureSession(provider, [new ValidationGate("build", Fail)], new WorkspaceCommands(Pass, Pass, _ => Pass), maxRepairRounds: 2);

        var proposal = await session.RunAsync(_repo, "Casse", "Échoue.", "test");

        Assert.Equal(FeatureStatus.GatesFailed, proposal.Status);
        Assert.Equal(4, provider.Requests.Count);
        Assert.Contains("Les vérifications ne passent pas", provider.Requests[2].Messages[^1].Text);

        await proposal.Workspace.RejectAsync();
        Assert.False(Directory.Exists(proposal.Workspace.Path));
    }

    sealed class FailingProvider : IChatProvider
    {
        public string Name => "panne";
        public string Model => "panne";
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            throw new HttpRequestException("serveur injoignable");
    }

    [Fact]
    public async Task A_provider_failure_leaves_no_worktree_nor_branch_and_the_title_can_be_reused()
    {
        var failing = new FeatureSession(new FailingProvider(), [new ValidationGate("build", Pass)], new WorkspaceCommands(Pass, Pass, _ => Pass));
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.RunAsync(_repo, "Même titre", "x", "test"));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".poiesis", "worktrees", "meme-titre")));

        // Une branche refusée est gardée : la feature suivante du même nom prend un suffixe.
        var provider = new ScriptedProvider(ScriptedProvider.Reply(StopReason.EndTurn, new TextPart("rien à faire")));
        var first = await new FeatureSession(provider, [new ValidationGate("build", Pass)], new WorkspaceCommands(Pass, Pass, _ => Pass))
            .RunAsync(_repo, "Même titre", "x", "test");
        Assert.Equal("feature/meme-titre", first.Workspace.Branch);
        await first.Workspace.RejectAsync();

        var again = new ScriptedProvider(ScriptedProvider.Reply(StopReason.EndTurn, new TextPart("rien à faire")));
        var second = await new FeatureSession(again, [new ValidationGate("build", Pass)], new WorkspaceCommands(Pass, Pass, _ => Pass))
            .RunAsync(_repo, "Même titre", "x", "test");
        Assert.Equal("feature/meme-titre-2", second.Workspace.Branch);
        await second.Workspace.RejectAsync();
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
