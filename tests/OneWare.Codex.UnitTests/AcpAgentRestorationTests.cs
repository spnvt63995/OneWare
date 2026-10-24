using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpAgentRestorationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshServiceRestoresTheSameChildWithoutReplayAndCountsOnlyTheFollowup(bool background)
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        var parent = h.Service.SendAsync("Initial delegation."); await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", background); await Ready(peers, 2);
        peers[1].Complete(new { inputTokens = 12, outputTokens = 3 });
        var id = Payload(await start).GetProperty("id").GetString()!;
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Any());
        await peers[0].CallTool("observe", AcpPluginCatalog.AgentResultTool, new { id, action = "wait" });
        peers[0].Complete(); await parent;
        var parentId = peers[0].Id; var childId = peers[1].Id;
        await h.Service.DisposeAsync();

        var events = new List<ChatEvent>();
        next.Enqueue(new AcpPeer { Id = parentId, ReplayOnResume = true });
        await using var restored = Recreate(h, peers, next, events);
        Assert.True(await restored.LoadSessionAsync(parentId));
        Assert.Equal(3, peers.Count); Assert.Empty(peers[2].Prompts); Assert.Empty(events);
        Assert.Equal(0, restored.SessionTotalTurns);
        var resume = restored.SendAsync("Continue with the remembered worker."); await Ready(peers, 3);
        Assert.Contains(id, peers[2].Prompts[0].GetRawText());
        next.Enqueue(new AcpPeer { Id = childId, ReplayOnResume = true });
        var follow = Follow(peers[2], id); await Ready(peers, 4);
        Assert.Equal(0, peers[3].NewSessions); Assert.Equal(1, peers[3].Resumes);
        Assert.Equal(childId, peers[3].Prompts[0].GetProperty("sessionId").GetString());
        peers[3].Complete(new { inputTokens = 7, outputTokens = 2 });
        Assert.Equal(2, Payload(await follow).GetProperty("turn").GetInt32());
        peers[2].Complete(); await resume;
        Assert.Single(events.OfType<ChatSubAgentStartedEvent>());
        Assert.Equal(id + "-turn-2", events.OfType<ChatSubAgentStartedEvent>().Single().Id);
        Assert.DoesNotContain(events.OfType<ChatMessageDeltaEvent>(), x => x.Content == "old history");
        Assert.Equal(7, restored.SessionTotalInputTokens); Assert.Equal(2, restored.SessionTotalOutputTokens);

        await restored.DisposeAsync();
        var saved = h.Profile.LoadDelegations(parentId, h.Workspace);
        Assert.False(saved.Incomplete); Assert.Equal(2, Assert.Single(saved.Records).Turn);
        Assert.Equal(childId, saved.Records[0].SessionId);
        await h.Service.DisposeAsync(); // An already closed owner cannot overwrite the newer turn.
        Assert.Equal(2, Assert.Single(h.Profile.LoadDelegations(parentId, h.Workspace).Records).Turn);
    }

    [AvaloniaFact]
    public async Task RestoredNestedChildrenKeepTheirAncestryAndOriginalToolBudget()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "leaf", Description = "Leaf.", Instructions = "Read only.", Tools = ["acp_fixture"] });
        var parent = h.Service.SendAsync("Nested delegation."); await Ready(peers, 1);
        var worker = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        var leaf = Delegate(peers[1], "leaf", false); await Ready(peers, 3);
        peers[2].Complete(); var leafId = Payload(await leaf).GetProperty("id").GetString()!;
        peers[1].Complete(); var workerId = Payload(await worker).GetProperty("id").GetString()!;
        peers[0].Complete(); await parent; await h.Service.DisposeAsync();
        next.Enqueue(new AcpPeer { Id = peers[0].Id });
        await using var restored = Recreate(h, peers, next, []);
        h.Provider.RegisterFunction(new OneWareAiFunction { Name = "outside_budget", Description = "Outside.", IsReadOnly = true,
            Handler = (Func<string>)(() => "outside") });
        Assert.True(await restored.LoadSessionAsync(peers[0].Id));
        h.Agent = new ChatAgentDefinition { Id = "ask", DisplayName = "Ask", IsReadOnly = true };
        var resume = restored.SendAsync("Continue read-only."); await Ready(peers, 4);
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        var workerFollow = Follow(peers[3], workerId); await Ready(peers, 5);
        next.Enqueue(new AcpPeer { Id = peers[2].Id });
        var leafFollow = Follow(peers[4], leafId); await Ready(peers, 6);
        Assert.True(Error(await Follow(peers[5], workerId)));
        Assert.True(Error(await peers[5].CallTool("outside", "outside_budget", new { })));
        Assert.False(Error(await peers[5].CallTool("read", "acp_fixture", new { value = "current" })));
        Assert.Equal(peers[0].Id, Assert.Single(h.Starts).SessionId);
        peers[5].Complete(); Assert.False(Error(await leafFollow));
        peers[4].Complete(); Assert.False(Error(await workerFollow));
        peers[3].Complete(); await resume;
    }

    [AvaloniaTheory]
    [InlineData("foreign-owner")]
    [InlineData("missing-binding")]
    [InlineData("invalid-json")]
    public async Task UnusableSavedHandlesDoNotPreventParentResumeOrCreateReplacementAgents(string fault)
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        var parent = h.Service.SendAsync("Delegate."); await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        peers[1].Complete(); var id = Payload(await start).GetProperty("id").GetString()!;
        peers[0].Complete(); await parent; await h.Service.DisposeAsync();
        var file = Assert.Single(Directory.GetFiles(Path.Combine(h.Profile.Directory, "delegations"), "*.json", SearchOption.AllDirectories));
        if (fault == "invalid-json") File.WriteAllText(file, "{");
        else if (fault == "missing-binding") File.Delete(Path.Combine(h.Profile.Bindings, peers[1].Id + ".json"));
        else
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!; json["OwnerSession"] = Guid.NewGuid().ToString();
            File.WriteAllText(file, json.ToJsonString());
        }
        var preserved = File.ReadAllText(file);
        var events = new List<ChatEvent>(); next.Enqueue(new AcpPeer { Id = peers[0].Id });
        await using var restored = Recreate(h, peers, next, events);
        Assert.True(await restored.LoadSessionAsync(peers[0].Id));
        Assert.Single(events.OfType<ChatErrorEvent>()); Assert.Empty(peers[2].Prompts);
        var resume = restored.SendAsync("Follow up."); await Ready(peers, 3);
        Assert.True(Error(await Follow(peers[2], id))); Assert.Equal(3, peers.Count);
        Assert.Equal(preserved, File.ReadAllText(file));
        peers[2].Complete(); await resume;
    }

    [AvaloniaFact]
    public async Task InterruptedChildCanResumeExplicitlyButACancelledChildStaysCancelled()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        var parent = h.Service.SendAsync("Background work."); await Ready(peers, 1);
        var first = Payload(await Delegate(peers[0], "worker", true)); await Ready(peers, 2);
        var id = first.GetProperty("id").GetString()!;
        var cancelled = Payload(await peers[0].CallTool("second", AcpPluginCatalog.DelegateTool,
            new { agent = "worker", task = "Cancel this task.", background = true })).GetProperty("id").GetString()!;
        await Ready(peers, 3);
        await peers[0].CallTool("cancel", AcpPluginCatalog.AgentResultTool, new { id = cancelled, action = "cancel" });
        peers[0].Complete(); await parent; await h.Service.DisposeAsync();
        next.Enqueue(new AcpPeer { Id = peers[0].Id });
        await using var restored = Recreate(h, peers, next, []);
        Assert.True(await restored.LoadSessionAsync(peers[0].Id)); Assert.Empty(peers[3].Prompts);
        var resume = restored.SendAsync("Continue interrupted work."); await Ready(peers, 4);
        Assert.Equal("interrupted", Payload(await peers[3].CallTool("status", AcpPluginCatalog.AgentResultTool,
            new { id })).GetProperty("status").GetString());
        Assert.True(Error(await Follow(peers[3], cancelled)));
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        var follow = Follow(peers[3], id, "continue"); await Ready(peers, 5);
        peers[4].Complete(); Assert.False(Error(await follow)); peers[3].Complete(); await resume;
    }

    [AvaloniaFact]
    public async Task RestoredHandleNeverReplacesAMissingRuntimeSessionAndCanRetry()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        var parent = h.Service.SendAsync("Delegate."); await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        peers[1].Complete(); var id = Payload(await start).GetProperty("id").GetString()!;
        peers[0].Complete(); await parent; await h.Service.DisposeAsync();
        next.Enqueue(new AcpPeer { Id = peers[0].Id });
        await using var restored = Recreate(h, peers, next, []);
        Assert.True(await restored.LoadSessionAsync(peers[0].Id));
        var resume = restored.SendAsync("Follow up."); await Ready(peers, 3);
        var failed = new AcpPeer { Id = peers[1].Id, FailResume = true }; next.Enqueue(failed);
        Assert.True(Error(await Follow(peers[2], id))); Assert.Equal(0, failed.NewSessions); Assert.Empty(failed.Prompts);
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        var retry = Follow(peers[2], id, "retry"); await Ready(peers, 5);
        Assert.Equal(0, peers[4].NewSessions); Assert.Equal(1, peers[4].Resumes);
        peers[4].Complete(); Assert.False(Error(await retry)); peers[2].Complete(); await resume;
    }

    private static AcpHarness Harness(List<AcpPeer> peers, Queue<AcpPeer> next) => new(factory: Factory(peers, next));
    private static Func<IAgentConnection> Factory(List<AcpPeer> peers, Queue<AcpPeer> next) => () =>
    {
        var peer = next.Count == 0 ? new AcpPeer() : next.Dequeue(); peers.Add(peer); return peer;
    };
    private static AcpChatService Recreate(AcpHarness h, List<AcpPeer> peers, Queue<AcpPeer> next, List<ChatEvent> events)
    {
        var service = new AcpChatService(h.Host, h.Profile with { }, () => h.Agent, Factory(peers, next));
        service.EventReceived += (_, e) => events.Add(e);
        return service;
    }
    private static void Register(AcpHarness h) => h.Provider.RegisterAgent(new OneWareAiAgent
    {
        Name = "worker", DisplayName = "Worker", Description = "Saved child.", Instructions = "Keep the same task.",
        Tools = ["acp_fixture"], Model = "gpt-6-luna", ReasoningEffort = "low"
    });
    private static Task Ready(List<AcpPeer> peers, int count) =>
        AcpHarness.Until(() => peers.Count >= count && peers[count - 1].Prompts.Count == 1);
    private static Task<JsonElement> Delegate(AcpPeer peer, string agent, bool background) =>
        peer.CallTool("delegate-" + agent, AcpPluginCatalog.DelegateTool, new { agent, task = "Initial task.", background });
    private static Task<JsonElement> Follow(AcpPeer peer, string id, string call = "followup") =>
        peer.CallTool(call, AcpPluginCatalog.FollowupTool, new { id, task = "Additional instructions." });
    private static bool Error(JsonElement response) => response.GetProperty("isError").GetBoolean();
    private static JsonElement Payload(JsonElement response)
    {
        Assert.False(Error(response));
        return JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }
}
