using System.Text.Json;
using System.Text.Json.Nodes;
using Poiesis.AI.Agent;
using Poiesis.AI.Features;
using Poiesis.AI.Providers;

namespace Poiesis.AI.Tests;

/// <summary>Fournisseur scripté : rejoue des réponses prédéfinies et garde les requêtes reçues.</summary>
sealed class ScriptedProvider(params ChatResponse[] responses) : IChatProvider
{
    readonly Queue<ChatResponse> _responses = new(responses);

    public List<ChatRequest> Requests { get; } = new();
    public string Name => "scripted";
    public string Model => "scripted";

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.Dequeue());
    }

    public static ChatResponse Reply(StopReason stop, params ChatPart[] parts) =>
        new(new ChatMessage(ChatRole.Assistant, parts), stop, new Usage(10, 5), "scripted");
}

public class AgentLoopTests
{
    static readonly JsonElement EchoArgs = JsonSerializer.SerializeToElement(new { text = "salut" });

    static IAgentTool Echo() => new DelegateTool(
        new ToolDefinition("echo", "Renvoie le texte.", DelegateTool.StringArgs(("text", "Texte."))),
        (args, _) => Task.FromResult(DelegateTool.TryGetString(args, "text", out var t) ? new ToolOutcome(t) : ToolOutcome.Error("text manquant")));

    [Fact]
    public async Task Runs_tools_then_returns_all_results_in_one_user_message()
    {
        var provider = new ScriptedProvider(
            ScriptedProvider.Reply(StopReason.ToolUse, new ToolCallPart("1", "echo", EchoArgs), new ToolCallPart("2", "missing", EchoArgs)),
            ScriptedProvider.Reply(StopReason.EndTurn, new TextPart("fini")));

        var result = await new AgentLoop(provider, [Echo()]).RunAsync("sys", [ChatMessage.User("go")]);

        Assert.Equal(StopReason.EndTurn, result.Reason);
        Assert.Equal(2, result.Turns);
        var toolResults = provider.Requests[1].Messages[^1].Parts.Cast<ToolResultPart>().ToList();
        Assert.Equal(2, toolResults.Count);
        Assert.Equal("salut", toolResults[0].Content);
        Assert.True(toolResults[1].IsError);
        Assert.Equal(new Usage(20, 10), result.Usage);
    }

    [Fact]
    public async Task Does_not_run_tools_from_a_truncated_response()
    {
        int calls = 0;
        var tool = new DelegateTool(new ToolDefinition("echo", "", DelegateTool.StringArgs()), (_, _) => { calls++; return Task.FromResult(new ToolOutcome("")); });
        var provider = new ScriptedProvider(ScriptedProvider.Reply(StopReason.MaxTokens, new ToolCallPart("1", "echo", EchoArgs)));

        var result = await new AgentLoop(provider, [tool]).RunAsync("sys", [ChatMessage.User("go")]);

        Assert.Equal(StopReason.MaxTokens, result.Reason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Workspace_paths_cannot_escape_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "poiesis-ws");
        var tools = new WorkspaceTools(root, WorkspaceCommands.DotNet("x.slnx", "h"));
        Assert.True(tools.TryResolve("content/ships/a.json", out _));
        Assert.False(tools.TryResolve("../outside.txt", out _));
        Assert.False(tools.TryResolve("/etc/passwd", out _));
    }

    [Theory]
    [InlineData("Boucliers à 6 faces !", "boucliers-a-6-faces")]
    [InlineData("  ", "feature")]
    public void Feature_titles_become_branch_slugs(string title, string slug) =>
        Assert.Equal(slug, FeatureWorkspace.Slugify(title));
}

public class OpenAiCompatibleProviderTests
{
    [Fact]
    public void Tool_results_become_tool_messages_and_tool_calls_round_trip()
    {
        var provider = new OpenAiCompatibleProvider(
            new ProviderSettings { Name = "Ollama", Kind = ProviderKind.OpenAiCompatible, Model = "m", BaseUrl = "http://localhost:11434/v1" },
            new HttpClient());
        var args = JsonSerializer.SerializeToElement(new { path = "a.txt" });
        var request = new ChatRequest("sys",
            [
                ChatMessage.User("lis a.txt"),
                new ChatMessage(ChatRole.Assistant, [new ToolCallPart("c1", "read_file", args)]),
                new ChatMessage(ChatRole.User, [new ToolResultPart("c1", "contenu")]),
            ],
            [new ToolDefinition("read_file", "Lit.", DelegateTool.StringArgs(("path", "Chemin.")))]);

        var body = provider.BuildBody(request);
        var messages = body["messages"]!.AsArray();

        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("read_file", messages[2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal("c1", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_tool_calls_and_tolerates_invalid_arguments()
    {
        var root = JsonNode.Parse("""
            {
              "model": "gpt",
              "choices": [{ "finish_reason": "tool_calls", "message": { "content": null, "tool_calls": [
                { "id": "a", "type": "function", "function": { "name": "build", "arguments": "{}" } },
                { "id": "b", "type": "function", "function": { "name": "read_file", "arguments": "{pas du json" } }
              ] } }],
              "usage": { "prompt_tokens": 12, "completion_tokens": 3 }
            }
            """)!.AsObject();

        var response = OpenAiCompatibleProvider.ParseResponse(root, "gpt");

        Assert.Equal(StopReason.ToolUse, response.StopReason);
        var calls = response.Message.Parts.Cast<ToolCallPart>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].Arguments.TryGetProperty("_invalid_json", out _));
        Assert.Equal(new Usage(12, 3), response.Usage);
    }
}
