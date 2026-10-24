using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpAgentFollowupTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameChildResumesAcrossParentTurnsWithApprovalsHistoryAndOnlyNewUsage(bool background)
    {
        var peers = new List<AcpPeer>();
        var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next, confirm: true);
        Register(h);
        var (chat, session) = Host(h);
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "Delegate the first task.";
        var first = session.SendCommand.ExecuteAsync(null);
        await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", background, "FIRST_ONLY_MEMORY");
        await Ready(peers, 2);
        var tool = Read(peers[1], "same-call");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowForSessionCommand!.Execute(null);
        Assert.False(Error(await tool));
        peers[1].Complete(new { inputTokens = 10, outputTokens = 2 });
        var id = Payload(await start).GetProperty("id").GetString()!;
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Count() == 1);
        Assert.False(peers[1].IsConnected);
        if (background)
            Assert.False(Error(await peers[0].CallTool("first-result", AcpPluginCatalog.AgentResultTool, new { id })));
        peers[0].Complete(new { inputTokens = 1, outputTokens = 1 }); await first;

        var resumed = new AcpPeer { Id = peers[1].Id, ReplayOnResume = true };
        next.Enqueue(resumed);
        session.CurrentMessage = "Continue with the same child.";
        var second = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        Assert.Contains(id, peers[0].Prompts[1].GetRawText());
        var followup = Follow(peers[0], id, background, "Recall the earlier memory.");
        await Ready(peers, 3);
        Assert.Equal(1, resumed.Resumes);
        Assert.Equal(0, resumed.NewSessions);
        Assert.Equal(peers[1].Id, resumed.Prompts[0].GetProperty("sessionId").GetString());
        Assert.DoesNotContain("FIRST_ONLY_MEMORY", resumed.Prompts[0].GetRawText());
        Assert.False(Error(await Read(resumed, "same-call")));
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()); // Same child session's explicit grant.
        Assert.All(h.Starts, x => Assert.Equal(peers[0].Id, x.SessionId));
        Assert.Equal(2, h.Starts.Select(x => x.ToolCallId).Distinct().Count());
        resumed.Complete(new { inputTokens = 20, outputTokens = 3 });
        var result = Payload(await followup);
        Assert.Equal(id, result.GetProperty("id").GetString());
        Assert.Equal(2, result.GetProperty("turn").GetInt32());
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Count() == 2);
        Assert.False(Error(await peers[0].CallTool("latest", AcpPluginCatalog.AgentResultTool, new { id })));
        Assert.True(Error(await Follow(peers[0], id, background, "Recall the earlier memory."))); // Same call id.
        Assert.Equal(3, peers.Count);
        peers[0].Complete(new { inputTokens = 2, outputTokens = 1 }); await second;
        Assert.Equal(4, h.Service.SessionTotalTurns);
        Assert.Equal(33, h.Service.SessionTotalInputTokens);
        Assert.Equal(7, h.Service.SessionTotalOutputTokens);
        Assert.Equal(new long?[] { 12, 23 }, h.Events.OfType<ChatSubAgentCompletedEvent>().Select(x => x.TotalTokens));
        Dispatcher.UIThread.RunJobs();
        var blocks = session.Messages.OfType<ChatMessageSubAgentViewModel>().ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Contains("Follow-up 2", blocks[1].DisplayName);
        Assert.All(blocks, b => Assert.True(b.IsSuccessful));
        Assert.DoesNotContain(h.Events.OfType<ChatMessageDeltaEvent>(), x => x.Content == "old history");
        var before = h.Events.Count;
        peers[1].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "STALE" } });
        Assert.Equal(before, h.Events.Count);
        chat.SaveState();
        var history = string.Join("\n", Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("Follow-up 2", history);
        Assert.DoesNotContain("old history", history);
    }

    [AvaloniaFact]
    public async Task BusyForeignSelfAndCancelledHandlesCannotStartAnotherChildTurn()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        var parent = h.Service.SendAsync("Start background work."); await Ready(peers, 1);
        var id = Payload(await Delegate(peers[0], "worker", true)).GetProperty("id").GetString()!;
        await Ready(peers, 2);
        Assert.True(Error(await Follow(peers[0], id, false)));
        Assert.True(Error(await Follow(peers[1], id, false)));
        await using var other = (AcpChatService)h.Service.CreateSession();
        var independent = other.SendAsync("Another conversation."); await Ready(peers, 3);
        Assert.True(Error(await Follow(peers[2], id, false)));
        Assert.Equal(3, peers.Count);
        Assert.Equal("cancelled", Payload(await peers[0].CallTool("cancel", AcpPluginCatalog.AgentResultTool,
            new { id, action = "cancel" })).GetProperty("status").GetString());
        Assert.True(Error(await Follow(peers[0], id, false, call: "after-cancel")));
        peers[0].Complete(); await parent;
        peers[2].Complete(); await independent;
    }

    [AvaloniaFact]
    public async Task FollowupKeepsOriginalBudgetAndReappliesCurrentAskRestrictions()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h, ["acp_fixture", "write_fixture"]);
        var writes = 0;
        h.Provider.RegisterFunction(new OneWareAiFunction { Name = "write_fixture", Description = "Write fixture.",
            Handler = (Func<string>)(() => (++writes).ToString()) });
        h.Provider.RegisterFunction(new OneWareAiFunction { Name = "outside_budget", Description = "Outside child budget.",
            IsReadOnly = true, Handler = (Func<string>)(() => "outside") });
        var first = h.Service.SendAsync("Initial work."); await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        peers[1].Complete(); var id = Payload(await start).GetProperty("id").GetString()!;
        peers[0].Complete(); await first;
        h.Agent = new ChatAgentDefinition { Id = "ask", DisplayName = "Ask", IsReadOnly = true,
            Tools = ["acp_fixture", "write_fixture", "outside_budget"] };
        h.Register("REFRESHED_");
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        var second = h.Service.SendAsync("Continue read-only."); await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        var follow = Follow(peers[0], id, false); await Ready(peers, 3);
        Assert.True(Error(await peers[2].CallTool("write", "write_fixture", new { })));
        Assert.True(Error(await peers[2].CallTool("outside", "outside_budget", new { })));
        Assert.Contains("REFRESHED_value", (await Read(peers[2], "read")).GetRawText());
        Assert.Equal(0, writes);
        peers[2].Complete(); Assert.False(Error(await follow));
        peers[0].Complete(); await second;
    }

    [AvaloniaFact]
    public async Task FailedResumeNeverCreatesReplacementAndCanRetryTheSameSession()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        var parent = h.Service.SendAsync("Delegate and follow up."); await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        peers[1].Complete(); var id = Payload(await start).GetProperty("id").GetString()!;
        var failed = new AcpPeer { Id = peers[1].Id, FailResume = true }; next.Enqueue(failed);
        Assert.True(Error(await Follow(peers[0], id, false)));
        Assert.Equal(0, failed.NewSessions); Assert.Empty(failed.Prompts);
        var retry = new AcpPeer { Id = peers[1].Id }; next.Enqueue(retry);
        var follow = Follow(peers[0], id, false, call: "retry"); await Ready(peers, 4);
        Assert.Equal(1, retry.Resumes); Assert.Equal(0, retry.NewSessions);
        retry.Complete(); Assert.False(Error(await follow));
        peers[0].Complete(); await parent;
    }

    [AvaloniaFact]
    public async Task ResumedChildCanFollowItsOwnDescendantButCannotAddressItsParent()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next); Register(h);
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "leaf", Description = "Leaf fixture.", Instructions = "Use the fixture." });
        var parent = h.Service.SendAsync("Nested work."); await Ready(peers, 1);
        var worker = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        var leaf = Delegate(peers[1], "leaf", false); await Ready(peers, 3);
        peers[2].Complete(); var leafId = Payload(await leaf).GetProperty("id").GetString()!;
        peers[1].Complete(); var workerId = Payload(await worker).GetProperty("id").GetString()!;
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        var workerFollow = Follow(peers[0], workerId, false); await Ready(peers, 4);
        next.Enqueue(new AcpPeer { Id = peers[2].Id });
        var leafFollow = Follow(peers[3], leafId, false); await Ready(peers, 5);
        Assert.True(Error(await Follow(peers[4], workerId, false)));
        Assert.Equal(peers[2].Id, peers[4].Prompts[0].GetProperty("sessionId").GetString());
        peers[4].Complete(); Assert.False(Error(await leafFollow));
        peers[3].Complete(); Assert.False(Error(await workerFollow));
        peers[0].Complete(); await parent;
    }

    [AvaloniaFact]
    public async Task NewChatRetiresAnActiveFollowupAndRejectsItsLateApprovalAndHandle()
    {
        var peers = new List<AcpPeer>(); var next = new Queue<AcpPeer>();
        await using var h = Harness(peers, next, confirm: true); Register(h);
        var parent = h.Service.SendAsync("Initial and background follow-up."); await Ready(peers, 1);
        var start = Delegate(peers[0], "worker", false); await Ready(peers, 2);
        peers[1].Complete(); var id = Payload(await start).GetProperty("id").GetString()!;
        next.Enqueue(new AcpPeer { Id = peers[1].Id });
        Assert.False(Error(await Follow(peers[0], id, true))); await Ready(peers, 3);
        var pending = Read(peers[2], "late");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var decision = Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
        peers[0].Complete(); await parent;
        await h.Service.NewChatAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try { await pending; } catch (Exception e) when (e is HttpRequestException or JsonException) { }
        decision.AllowForSessionCommand!.Execute(null);
        Assert.Empty(h.Starts); Assert.All(peers, p => Assert.False(p.IsConnected));
        var fresh = h.Service.SendAsync("New conversation."); await Ready(peers, 4);
        Assert.DoesNotContain(id, peers[3].Prompts[0].GetRawText());
        Assert.True(Error(await Follow(peers[3], id, false)));
        peers[3].Complete(); await fresh;
    }

    private static AcpHarness Harness(List<AcpPeer> peers, Queue<AcpPeer> next, bool confirm = false) =>
        new(confirm: confirm, factory: () => { var p = next.Count > 0 ? next.Dequeue() : new AcpPeer(); peers.Add(p); return p; });
    private static void Register(AcpHarness h, string[]? tools = null) => h.Provider.RegisterAgent(new OneWareAiAgent
    {
        Name = "worker", DisplayName = "Worker", Description = "Follow-up fixture.", Instructions = "Use the fixture.",
        Tools = tools, Model = "gpt-6-luna", ReasoningEffort = "low"
    });
    private static Task Ready(List<AcpPeer> peers, int count) =>
        AcpHarness.Until(() => peers.Count >= count && peers[count - 1].Prompts.Count == 1);
    private static Task<JsonElement> Delegate(AcpPeer peer, string agent, bool background, string task = "Initial task.") =>
        peer.CallTool("delegate-" + agent, AcpPluginCatalog.DelegateTool, new { agent, task, background });
    private static Task<JsonElement> Read(AcpPeer peer, string id) =>
        peer.CallTool(id, "acp_fixture", new { value = "value" });
    private static Task<JsonElement> Follow(AcpPeer peer, string id, bool background, string task = "Additional instructions.", string call = "followup") =>
        peer.CallTool(call, AcpPluginCatalog.FollowupTool, new { id, task, background });
    private static bool Error(JsonElement response) => response.GetProperty("isError").GetBoolean();
    private static JsonElement Payload(JsonElement response)
    {
        Assert.False(Error(response));
        return JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }
    private static (ChatViewModel Chat, ChatSessionViewModel Session) Host(AcpHarness h)
    {
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
        var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? Path.Combine(h.Workspace, "chat-host") : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
            "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        chat.RegisterChatService(h.Service); chat.InitializeContent();
        return (chat, chat.OpenNewChat()!);
    }
}
