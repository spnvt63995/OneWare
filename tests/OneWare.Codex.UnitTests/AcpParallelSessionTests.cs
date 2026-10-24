using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Codex.Services;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpParallelSessionTests
{
    [AvaloniaFact]
    public async Task OriginalChatHostRunsTwoConversationsAndKeepsTheirHistorySeparate()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
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
        chat.RegisterChatService(h.Service);
        chat.InitializeContent();
        try
        {
            var a = chat.OpenNewChat()!;
            await AcpHarness.Until(() => a.IsConnected);
            a.CurrentMessage = "PARALLEL_CHAT_A";
            var first = a.SendCommand.ExecuteAsync(null);
            await AcpHarness.Until(() => peers[0].Prompts.Count == 1);
            var b = chat.OpenNewChat()!;
            Assert.NotSame(a, b);
            Assert.NotSame(a.Service, b.Service);
            Assert.Same(h.Service, b.RegisteredService);
            Assert.True(a.IsBusy);
            await AcpHarness.Until(() => b.IsConnected);
            b.CurrentMessage = "PARALLEL_CHAT_B";
            var second = b.SendCommand.ExecuteAsync(null);
            await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
            Assert.True(a.IsBusy && b.IsBusy);
            peers[0].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "ONLY_A:" } });
            peers[1].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "ONLY_B:" } });
            peers[1].Complete();
            await second;
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.IsBusy);
            Assert.True(a.IsBusy);
            Assert.Contains(b.Messages.OfType<ChatMessageAssistantViewModel>(), x => x.Content == "ONLY_B:answer");
            Assert.DoesNotContain(b.Messages.OfType<ChatMessageAssistantViewModel>(), x => x.Content.Contains("ONLY_A"));
            chat.SelectedOverviewItem = chat.Overview.Single(x => x.Title == "PARALLEL_CHAT_A");
            await AcpHarness.Until(() => ReferenceEquals(chat.CurrentSession, a));
            await AcpHarness.Until(() => !peers[1].IsConnected);
            Assert.True(peers[0].IsConnected);
            peers[0].Complete();
            await first;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(a.Messages.OfType<ChatMessageAssistantViewModel>(), x => x.Content == "ONLY_A:answer");
            chat.SaveState();
            var history = Directory.GetFiles(directory, "chat_*.json", SearchOption.AllDirectories)
                .Select(File.ReadAllText).ToArray();
            Assert.Contains(history, x => x.Contains("PARALLEL_CHAT_A") && x.Contains("ONLY_A") && !x.Contains("ONLY_B"));
            Assert.Contains(history, x => x.Contains("PARALLEL_CHAT_B") && x.Contains("ONLY_B") && !x.Contains("ONLY_A"));
        }
        finally { chat.SaveState(); }
    }

    [AvaloniaFact]
    public async Task EachConversationOwnsOptionsAttachmentsViewsAndUsageButNewChatsUseLatestDefaults()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(settings: AcpTestSupport.Settings(),
            factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        var a = h.Service;
        await a.RefreshOptionsAsync();
        await a.SelectReasoningAsync(a.ReasoningEfforts.Single(x => x.Value == "medium"));
        await using var b = Assert.IsType<AcpChatService>(a.CreateSession());
        Assert.Equal("medium", b.ReasoningLabel);
        await b.RefreshOptionsAsync();
        await b.SelectReasoningAsync(b.ReasoningEfforts.Single(x => x.Value == "low"));
        b.SelectedApprovalMode = AcpChatService.ApprovalModeBypass;
        Assert.Equal("medium", a.ReasoningLabel);
        Assert.Equal(AcpChatService.ApprovalModeDefault, a.SelectedApprovalMode);
        Assert.NotSame(a.TopUiExtension, b.TopUiExtension);
        Assert.NotSame(a.BottomUiExtension, b.BottomUiExtension);
        Assert.NotSame(a.FooterUiExtension, b.FooterUiExtension);
        var attachment = Path.Combine(h.Workspace, "a-only.txt");
        File.WriteAllText(attachment, "ATTACHMENT_ONLY_A");
        a.Attachments.AddFiles([attachment]);
        Assert.Empty(b.Attachments.Items);
        var first = a.SendAsync("A");
        var second = b.SendAsync("B");
        await AcpHarness.Until(() => peers.All(x => x.Prompts.Count == 1));
        Assert.Contains("ATTACHMENT_ONLY_A", peers[0].Prompts[0].GetRawText());
        Assert.DoesNotContain("ATTACHMENT_ONLY_A", peers[1].Prompts[0].GetRawText());
        peers[0].Update(new { sessionUpdate = "usage_update", used = 100, size = 1000 });
        peers[1].Update(new { sessionUpdate = "usage_update", used = 200, size = 2000 });
        peers[0].Complete(new { inputTokens = 11, outputTokens = 1 });
        peers[1].Complete(new { inputTokens = 22, outputTokens = 2 });
        await Task.WhenAll(first, second);
        Assert.NotEqual(a.CurrentSessionId, b.CurrentSessionId);
        Assert.Equal(11, a.SessionTotalInputTokens);
        Assert.Equal(22, b.SessionTotalInputTokens);
        Assert.Equal(100, a.ContextCurrentTokens);
        Assert.Equal(200, b.ContextCurrentTokens);
        await a.NewChatAsync();
        Assert.Equal("low", a.ReasoningLabel);
        Assert.Equal(AcpChatService.ApprovalModeBypass, a.SelectedApprovalMode);
        Assert.False(a.HasUsageData);
        Assert.Equal(22, b.SessionTotalInputTokens);
        Assert.True(peers[1].IsConnected);
    }

    [AvaloniaFact]
    public async Task ApprovalsCallsCancellationAndQueuesStayWithTheirOriginatingConversation()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(confirm: true,
            factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        await using var b = Assert.IsType<AcpChatService>(h.Service.CreateSession());
        var events = new List<ChatEvent>();
        b.EventReceived += (_, e) => events.Add(e);
        var first = h.Service.SendAsync("A");
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        var second = b.SendAsync("B");
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        await h.Service.SendAsync("queued A", ChatSendMode.Queue);
        await b.SendAsync("queued B", ChatSendMode.Queue);
        peers[0].Tool("same-call-id");
        peers[1].Tool("same-call-id");
        var callA = peers[0].Call("same-call-id");
        var callB = peers[1].Call("same-call-id");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any() &&
                                    events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        Assert.False((await callA).GetProperty("isError").GetBoolean());
        Assert.False(callB.IsCompleted);
        await h.Service.AbortAsync();
        await first;
        Assert.False(peers[0].IsConnected);
        Assert.True(peers[1].IsConnected);
        Assert.False(second.IsCompleted);
        Assert.False(callB.IsCompleted);
        Assert.Single(events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        Assert.False((await callB).GetProperty("isError").GetBoolean());
        Assert.Equal(2, h.Starts.Count);
        peers[1].Complete();
        await AcpHarness.Until(() => peers[1].Prompts.Count == 2);
        Assert.Contains("queued B", peers[1].Prompts[1].GetRawText());
        Assert.Single(peers[0].Prompts);
        peers[1].Complete();
        await second;
        Assert.Empty(events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task ClosingOneSessionCancelsItsInitializationAndProviderShutdownClosesAllRemainingSessions()
    {
        var aPeer = new AcpPeer();
        var waitingPeer = new AcpPeer { BlockInitialization = true };
        var cPeer = new AcpPeer();
        var available = new Queue<AcpPeer>([aPeer, waitingPeer, cPeer]);
        await using var h = new AcpHarness(factory: () => available.Dequeue());
        var first = h.Service.SendAsync("A");
        await aPeer.PromptStarted.Task;
        var waiting = Assert.IsType<AcpChatService>(h.Service.CreateSession());
        var initialization = waiting.InitializeAsync();
        await waitingPeer.Initializing.Task;
        await waiting.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await initialization);
        Assert.True(aPeer.IsConnected);
        Assert.False(first.IsCompleted);
        Assert.False(waitingPeer.IsConnected);
        var c = Assert.IsType<AcpChatService>(h.Service.CreateSession());
        var third = c.SendAsync("C");
        await cPeer.PromptStarted.Task;
        await Task.WhenAll(h.Service.DisposeAsync().AsTask(), h.Service.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, third);
        Assert.False(aPeer.IsConnected);
        Assert.False(cPeer.IsConnected);
        Assert.Throws<ObjectDisposedException>(() => h.Service.CreateSession());
        Assert.Throws<ObjectDisposedException>(() => c.CreateSession());
    }
}
