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

public sealed class AcpBackgroundTests
{
    [AvaloniaFact]
    public async Task ParentContinuesWhileBackgroundApprovalAndToolRemainOwnedByTheOriginalBlock()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "background_read", FriendlyName = "Background read", Description = "Controlled fixture read.",
            IsReadOnly = true, ConfirmationCheck = _ => "Allow the background fixture read?",
            Handler = (Func<CancellationToken, Task<string>>)(async token =>
            {
                await release.Task.WaitAsync(token);
                return "BACKGROUND_TOOL_OK";
            })
        });
        h.Provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "expert", Description = "Background fixture.", Instructions = "Read the fixture.",
            Tools = ["background_read"], Model = "gpt-6-luna", ReasoningEffort = "low"
        });
        var (chat, directory) = Host(h);
        var session = chat.OpenNewChat()!;
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "Delegate in the background.";
        var first = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        var id = await Start(peers[0], "start");
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var read = peers[1].CallTool("read", "background_read", new { });
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        peers[0].Complete(); await first;
        Dispatcher.UIThread.RunJobs();
        var block = Assert.Single(session.Messages.OfType<ChatMessageSubAgentViewModel>());
        Assert.True(block.IsBackground);
        Assert.True(block.IsRunning);
        Assert.False(session.IsBusy);
        Assert.False(read.IsCompleted);

        // The user can send again while the child is waiting for their approval.
        session.CurrentMessage = "Continue the parent conversation.";
        var second = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        Assert.Contains(id, peers[0].Prompts[1].GetRawText());
        Assert.Equal("running", Payload(await peers[0].CallTool("status", AcpPluginCatalog.AgentResultTool,
            new { id })).GetProperty("status").GetString());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        await AcpHarness.Until(() => h.Starts.Count == 1);
        Assert.Equal(peers[0].Id, h.Starts[0].SessionId);
        Assert.StartsWith(id + ":", h.Starts[0].ToolCallId);
        peers[0].Complete(); await second;
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsBusy);
        Assert.True(block.IsRunning);
        Assert.False(read.IsCompleted); // Ending the parent must not cancel an executing child tool.
        session.CurrentMessage = "Unsent draft stays here.";
        var attachment = Path.Combine(h.Workspace, "unsent.txt");
        File.WriteAllText(attachment, "UNSENT_ATTACHMENT");
        h.Service.Attachments.AddFiles([attachment]);
        release.SetResult(true);
        Assert.False((await read).GetProperty("isError").GetBoolean());
        peers[1].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "BACKGROUND_RESULT:" } });
        peers[1].Complete();
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Any());
        Dispatcher.UIThread.RunJobs();
        Assert.False(block.IsRunning);
        Assert.True(block.IsSuccessful);
        Assert.Contains(block.Items.OfType<ChatMessageAssistantViewModel>(), x => x.Content == "BACKGROUND_RESULT:answer");
        Assert.Single(block.Items.OfType<ChatMessageToolViewModel>());
        Assert.DoesNotContain(session.Messages.OfType<ChatMessageAssistantViewModel>(), x => x.Content.Contains("BACKGROUND_RESULT"));
        Assert.Equal(2, h.Events.OfType<ChatIdleEvent>().Count());

        await AcpHarness.Until(() => peers[0].Prompts.Count == 3);
        Assert.Contains("BACKGROUND_RESULT:answer", peers[0].Prompts[2].GetRawText());
        Assert.Equal(peers[0].Id, peers[0].Prompts[2].GetProperty("sessionId").GetString());
        Assert.Equal("Unsent draft stays here.", session.CurrentMessage);
        Assert.Single(h.Service.Attachments.Items);
        Assert.DoesNotContain("UNSENT_ATTACHMENT", peers[0].Prompts[2].GetRawText());
        Assert.Equal(2, h.Events.OfType<ChatUserMessageEvent>().Count());
        peers[0].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "PARENT_DELIVERED:" } });
        peers[0].Complete();
        await AcpHarness.Until(() => h.Events.OfType<ChatIdleEvent>().Count() == 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(session.Messages.OfType<ChatMessageAssistantViewModel>(), x => x.Content == "PARENT_DELIVERED:answer");

        session.CurrentMessage = "Use the completed background result.";
        var third = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers[0].Prompts.Count == 4);
        Assert.DoesNotContain("BACKGROUND_RESULT:answer", peers[0].Prompts[3].GetRawText());
        var result = Payload(await peers[0].CallTool("result", AcpPluginCatalog.AgentResultTool, new { id, action = "wait" }));
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal("BACKGROUND_RESULT:answer", result.GetProperty("result").GetString());
        peers[0].Complete(); await third;
        Dispatcher.UIThread.RunJobs(); chat.SaveState();
        var history = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "chat_*.json", SearchOption.AllDirectories)));
        Assert.Contains("BACKGROUND_RESULT:answer", history);
        Assert.Contains("Background read", history);
        Assert.Contains("PARENT_DELIVERED:answer", history);
        Assert.False(peers[1].IsConnected);
    }

    [AvaloniaFact]
    public async Task ParentStopPreservesBackgroundButExplicitCancelIsScopedAndRejectsStaleApproval()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(confirm: true, factory: () =>
        {
            // A new transport resumes the same backend session after the parent is stopped.
            var peer = peers.Count == 3 ? new AcpPeer { Id = peers[0].Id } : new AcpPeer();
            peers.Add(peer); return peer;
        });
        Register(h);
        var first = h.Service.SendAsync("Start background work.");
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        var id = await Start(peers[0], "start");
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var pending = peers[1].CallTool("pending", "acp_fixture", new { value = "value" });
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var decision = Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
        await using var other = Assert.IsType<AcpChatService>(h.Service.CreateSession());
        var otherRun = other.SendAsync("Independent conversation.");
        await AcpHarness.Until(() => peers.Count == 3 && peers[2].Prompts.Count == 1);
        Assert.True((await peers[2].CallTool("foreign", AcpPluginCatalog.AgentResultTool,
            new { id, action = "cancel" })).GetProperty("isError").GetBoolean());
        var waiting = peers[0].CallTool("wait", AcpPluginCatalog.AgentResultTool, new { id, action = "wait" });
        await h.Service.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await first; await ClosedResponse(waiting);
        Assert.True(peers[1].IsConnected);
        Assert.False(pending.IsCompleted);
        Assert.True(peers[2].IsConnected);

        var followup = h.Service.SendAsync("Cancel my background task.");
        await AcpHarness.Until(() => peers.Count == 4 && peers[3].Prompts.Count == 1);
        var cancelled = Payload(await peers[3].CallTool("cancel", AcpPluginCatalog.AgentResultTool, new { id, action = "cancel" }));
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        await ClosedResponse(pending);
        decision.AllowCommand.Execute(null);
        Assert.Empty(h.Starts);
        Assert.False(peers[1].IsConnected);
        Assert.True(peers[2].IsConnected);
        Assert.False(otherRun.IsCompleted);
        var before = h.Events.Count;
        peers[1].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "STALE_BACKGROUND" } });
        Assert.Equal(before, h.Events.Count);
        Assert.Single(h.Events.OfType<ChatSubAgentCompletedEvent>(), x => x.Id == id && x.Cancelled);
        peers[3].Complete(); await followup;
        peers[2].Complete(); await otherRun;
    }

    [AvaloniaFact]
    public async Task ResetAndDisposalRetireOwnedBackgroundWorkAndFailuresStayInItsBlock()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        Register(h);
        var first = h.Service.SendAsync("Background work.");
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        var id = await Start(peers[0], "start");
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        peers[0].Complete(); await first;
        await h.Service.NewChatAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(peers[1].IsConnected);
        var afterReset = h.Events.Count;
        peers[1].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "RETIRED" } });
        Assert.Equal(afterReset, h.Events.Count);
        var second = h.Service.SendAsync("New conversation.");
        await AcpHarness.Until(() => peers.Count == 3 && peers[2].Prompts.Count == 1);
        Assert.DoesNotContain(id, peers[2].Prompts[0].GetRawText());
        Assert.True((await peers[2].CallTool("old", AcpPluginCatalog.AgentResultTool, new { id })).GetProperty("isError").GetBoolean());
        var failed = await Start(peers[2], "failed");
        await AcpHarness.Until(() => peers.Count == 4 && peers[3].Prompts.Count == 1);
        peers[3].Drop();
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Any(x => x.Id == failed));
        Assert.Contains(h.Events.OfType<ChatErrorEvent>(), x => x.AgentId == failed && x.Message!.Contains("connection lost"));
        Assert.DoesNotContain(h.Events.OfType<ChatErrorEvent>(), x => x.AgentId == null);
        Assert.Equal("failed", Payload(await peers[2].CallTool("failure-result", AcpPluginCatalog.AgentResultTool,
            new { id = failed })).GetProperty("status").GetString());
        var live = await Start(peers[2], "live");
        await AcpHarness.Until(() => peers.Count == 5 && peers[4].Prompts.Count == 1);
        await h.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await second;
        Assert.All(peers, p => Assert.False(p.IsConnected));
        Assert.NotEqual(failed, live);
    }

    private static AcpHarness Harness(List<AcpPeer> peers, bool confirm = false) => new(confirm: confirm,
        factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });

    private static void Register(AcpHarness h) => h.Provider.RegisterAgent(new OneWareAiAgent
        { Name = "expert", Description = "Background fixture.", Instructions = "Use OneWare tools." });

    private static async Task<string> Start(AcpPeer peer, string call) => Payload(await peer.CallTool(call,
        AcpPluginCatalog.DelegateTool, new { agent = "expert", task = "Check the fixture.", background = true }))
        .GetProperty("id").GetString()!;

    private static JsonElement Payload(JsonElement response)
    {
        Assert.False(response.GetProperty("isError").GetBoolean());
        return JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }

    private static async Task ClosedResponse(Task<JsonElement> response)
    {
        try { await response; } catch (Exception e) when (e is HttpRequestException or JsonException) { }
    }

    private static (ChatViewModel Chat, string Directory) Host(AcpHarness h)
    {
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
        var directory = Path.Combine(h.Workspace, "chat-host");
        var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? directory : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
            "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        chat.RegisterChatService(h.Service); chat.InitializeContent();
        return (chat, directory);
    }
}
