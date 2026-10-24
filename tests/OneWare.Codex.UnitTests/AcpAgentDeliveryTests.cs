using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Codex.Services;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpAgentDeliveryTests
{
    [AvaloniaFact]
    public async Task BusyParentCoalescesResultsIntoQueuedUserTurnAndKeepsItsCurrentRestrictions()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var writes = 0;
        h.Provider.RegisterFunction(new OneWareAiFunction { Name = "write_fixture", Description = "Write fixture.",
            Handler = (Func<string>)(() => (++writes).ToString()) });
        var send = h.Service.SendAsync("Start two tasks."); await Ready(peers, 1);
        await Start(peers[0], "one"); await Ready(peers, 2);
        await Start(peers[0], "two"); await Ready(peers, 3);
        h.Agent = new ChatAgentDefinition { Id = "ask", DisplayName = "Ask", IsReadOnly = true,
            Tools = ["acp_fixture", "write_fixture"] };
        await h.Service.SendAsync("Queued read-only request.", ChatSendMode.Queue);
        Answer(peers[1], "FIRST_RESULT:"); Answer(peers[2], "SECOND_RESULT:");
        await Completed(h, 2);
        Assert.Single(peers[0].Prompts);
        peers[0].Complete(); await Prompts(peers[0], 2);
        var prompt = peers[0].Prompts[1].GetRawText();
        Assert.Contains("Queued read-only request.", prompt);
        Assert.Contains("FIRST_RESULT:answer", prompt); Assert.Contains("SECOND_RESULT:answer", prompt);
        Assert.True(Error(await peers[0].CallTool("write", "write_fixture", new { })));
        Assert.Equal(0, writes);
        peers[0].Complete(); await send;
        Assert.Equal(2, peers[0].Prompts.Count);
        var next = h.Service.SendAsync("Next request."); await Prompts(peers[0], 3);
        Assert.DoesNotContain("FIRST_RESULT", peers[0].Prompts[2].GetRawText());
        Assert.DoesNotContain("SECOND_RESULT", peers[0].Prompts[2].GetRawText());
        peers[0].Complete(); await next;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundOrExplicitlyAwaitedResultDoesNotCreateAnExtraParentTurn(bool background)
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var parent = h.Service.SendAsync("Wait for the result."); await Ready(peers, 1);
        var start = peers[0].CallTool("start", AcpPluginCatalog.DelegateTool,
            new { agent = "worker", task = "Read.", background });
        await Ready(peers, 2);
        Task<JsonElement>? wait = null;
        if (background)
        {
            var id = Payload(await start).GetProperty("id").GetString();
            wait = peers[0].CallTool("wait", AcpPluginCatalog.AgentResultTool, new { id, action = "wait" });
            Assert.False(wait.IsCompleted);
        }
        Answer(peers[1], "OBSERVED_RESULT:");
        Assert.False(Error(await (wait ?? start))); await Completed(h, 1);
        peers[0].Complete(); await parent;
        Assert.Single(peers[0].Prompts);
        Assert.Single(h.Events.OfType<ChatUserMessageEvent>());
    }

    [AvaloniaFact]
    public async Task StopHoldsCompletionUntilUserResumesTheSameConversation()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next);
        var parent = h.Service.SendAsync("Start background work."); await Ready(peers, 1);
        await Start(peers[0]); await Ready(peers, 2);
        await h.Service.AbortAsync(); await parent;
        Answer(peers[1], "AFTER_STOP:"); await Completed(h, 1);
        Assert.Equal(2, peers.Count); Assert.Single(peers[0].Prompts);
        next.Enqueue(new AcpPeer { Id = peers[0].Id });
        var resumed = h.Service.SendAsync("Resume here."); await Ready(peers, 3);
        Assert.Equal(1, peers[2].Resumes);
        Assert.Contains("AFTER_STOP:answer", peers[2].Prompts[0].GetRawText());
        peers[2].Complete(); await resumed;
        Assert.Single(peers[2].Prompts);
    }

    [AvaloniaFact]
    public async Task NewConversationDropsCompletedResultsWaitingBehindAnActiveParent()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var parent = h.Service.SendAsync("Old task."); await Ready(peers, 1);
        var id = await Start(peers[0]); await Ready(peers, 2);
        Answer(peers[1], "OLD_RESULT:"); await Completed(h, 1);
        await h.Service.NewChatAsync(); await parent;
        var fresh = h.Service.SendAsync("Fresh conversation."); await Ready(peers, 3);
        Assert.DoesNotContain("OLD_RESULT", peers[2].Prompts[0].GetRawText());
        Assert.DoesNotContain(id, peers[2].Prompts[0].GetRawText());
        peers[2].Complete(); await fresh;
        Assert.Single(peers[0].Prompts); Assert.Single(peers[2].Prompts);
    }

    [AvaloniaFact]
    public async Task FailureNotifiesOnlyItsOwnerAndUserCanSendDuringAutomaticContinuation()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var parent = h.Service.SendAsync("Background work."); await Ready(peers, 1);
        await Start(peers[0]); await Ready(peers, 2);
        peers[0].Complete(); await parent;
        await using var other = (AcpChatService)h.Service.CreateSession();
        var independent = other.SendAsync("Independent."); await Ready(peers, 3);
        peers[2].Complete(); await independent;
        peers[1].Drop(); await Prompts(peers[0], 2);
        Assert.Contains("failed", peers[0].Prompts[1].GetRawText());
        Assert.Contains("connection lost", peers[0].Prompts[1].GetRawText());
        Assert.Single(peers[2].Prompts);
        Assert.Single(h.Events.OfType<ChatUserMessageEvent>());
        await h.Service.SendAsync("User can continue."); // The original host's Send action remains usable.
        peers[0].Complete(); await Prompts(peers[0], 3);
        Assert.Contains("User can continue.", peers[0].Prompts[2].GetRawText());
        Assert.DoesNotContain("connection lost", peers[0].Prompts[2].GetRawText());
        peers[0].Complete(); await Idle(h, 2);
        Assert.Equal(3, peers[0].Prompts.Count);
    }

    [AvaloniaFact]
    public async Task SameChildFollowupDeliversEachRoundOnceAndCountsOnlyNewUsage()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next);
        var parent = h.Service.SendAsync("Start."); await Ready(peers, 1);
        var id = await Start(peers[0]); await Ready(peers, 2);
        peers[0].Complete(new { inputTokens = 1, outputTokens = 1 }); await parent;
        Answer(peers[1], "ROUND_ONE:", new { inputTokens = 2, outputTokens = 2 });
        await Prompts(peers[0], 2);
        Assert.Contains("ROUND_ONE:answer", peers[0].Prompts[1].GetRawText());
        peers[0].Complete(new { inputTokens = 3, outputTokens = 3 }); await Idle(h, 2);
        var user = h.Service.SendAsync("Follow up."); await Prompts(peers[0], 3);
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        Assert.False(Error(await peers[0].CallTool("follow", AcpPluginCatalog.FollowupTool,
            new { id, task = "Next round.", background = true })));
        await Ready(peers, 3); Assert.Equal(1, peers[2].Resumes);
        peers[0].Complete(new { inputTokens = 4, outputTokens = 4 }); await user;
        Answer(peers[2], "ROUND_TWO:", new { inputTokens = 5, outputTokens = 5 });
        await Prompts(peers[0], 4);
        var completion = peers[0].Prompts[3].GetRawText();
        Assert.Contains("ROUND_TWO:answer", completion); Assert.DoesNotContain("ROUND_ONE", completion);
        peers[0].Complete(new { inputTokens = 6, outputTokens = 6 }); await Idle(h, 4);
        Assert.Equal(6, h.Service.SessionTotalTurns);
        Assert.Equal(21, h.Service.SessionTotalInputTokens); Assert.Equal(21, h.Service.SessionTotalOutputTokens);
        Assert.Equal(2, h.Events.OfType<ChatUserMessageEvent>().Count());
    }

    [AvaloniaFact]
    public async Task FailedAutomaticPromptDoesNotRetryAndResultCanStillBeRead()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var parent = h.Service.SendAsync("Start."); await Ready(peers, 1);
        var id = await Start(peers[0]); await Ready(peers, 2);
        peers[0].Complete(); await parent;
        peers[0].FailPrompt = true;
        Answer(peers[1], "RECOVERABLE_RESULT:"); await Idle(h, 2);
        Assert.Single(h.Events.OfType<ChatErrorEvent>(), e => e.AgentId == null);
        peers[0].FailPrompt = false;
        var user = h.Service.SendAsync("Read the result."); await Prompts(peers[0], 2);
        Assert.DoesNotContain("RECOVERABLE_RESULT", peers[0].Prompts[1].GetRawText());
        var result = Payload(await peers[0].CallTool("read", AcpPluginCatalog.AgentResultTool, new { id }));
        Assert.Equal("RECOVERABLE_RESULT:answer", result.GetProperty("result").GetString());
        peers[0].Complete(); await user;
        Assert.Equal(2, peers[0].Prompts.Count);
    }

    private static AcpHarness Harness(List<AcpPeer> peers, Queue<AcpPeer>? next = null)
    {
        var h = new AcpHarness(factory: () =>
        {
            var p = next?.Count > 0 ? next.Dequeue() : new AcpPeer(); peers.Add(p); return p;
        });
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "worker", Description = "Delivery fixture.",
            Instructions = "Read the fixture.", Model = "gpt-6-luna", ReasoningEffort = "low" });
        return h;
    }
    private static async Task<string> Start(AcpPeer p, string call = "start") =>
        Payload(await p.CallTool(call, AcpPluginCatalog.DelegateTool,
            new { agent = "worker", task = "Read.", background = true })).GetProperty("id").GetString()!;
    private static void Answer(AcpPeer p, string prefix, object? usage = null)
    {
        p.Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = prefix } });
        p.Complete(usage);
    }
    private static Task Ready(List<AcpPeer> peers, int n) =>
        AcpHarness.Until(() => peers.Count >= n && peers[n - 1].Prompts.Count == 1);
    private static Task Prompts(AcpPeer p, int n) => AcpHarness.Until(() => p.Prompts.Count == n);
    private static Task Completed(AcpHarness h, int n) =>
        AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Count() == n);
    private static Task Idle(AcpHarness h, int n) =>
        AcpHarness.Until(() => h.Events.OfType<ChatIdleEvent>().Count() == n);
    private static bool Error(JsonElement response) => response.GetProperty("isError").GetBoolean();
    private static JsonElement Payload(JsonElement response)
    {
        Assert.False(Error(response));
        return JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }
}
