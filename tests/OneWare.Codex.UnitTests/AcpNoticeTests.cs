using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpNoticeTests
{
    private static object Notice(string severity = "info", string title = "Runtime notice", string? description = null) =>
        new { sessionUpdate = "notice", severity, title, description };

    private static ChatNoticeViewModel[] Notices(ChatSessionViewModel session)
    {
        Dispatcher.UIThread.RunJobs();
        return session.Notices.Entries.ToArray();
    }

    [AvaloniaTheory]
    [InlineData("info", "Info")]
    [InlineData("warning", "Warning")]
    [InlineData("error", "Error")]
    [InlineData("_custom", "Notice")]
    [InlineData("future", "Notice")]
    public async Task NoticesArriveBeforeSessionReplyWhileIdleAndDuringWorkWithoutChangingControlState(string severity, string label)
    {
        await using var h = new AcpHarness();
        h.Peer.SessionUpdates = [Notice(severity, "Configuration notice", "Plain **text**, not Markdown")];
        var session = Host(h).CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        await h.Service.RefreshCommandsAsync();
        var first = Assert.Single(Notices(session));
        Assert.Equal(severity, first.Notice.Severity);
        Assert.Equal(label, first.SeverityText);
        Assert.Equal(severity == "error", first.IsError);
        Assert.Equal(severity == "warning", first.IsWarning);
        Assert.Empty(h.Peer.Prompts);
        var capability = h.Peer.Requests.Single(x => x.Method == "initialize").Parameters
            .GetProperty("clientCapabilities").GetProperty("session");
        Assert.Equal(JsonValueKind.Object, capability.GetProperty("notices").ValueKind);
        Assert.Equal(JsonValueKind.Object, capability.GetProperty("compaction").ValueKind);
        var selection = h.Profile.LoadSelection(h.Service.CurrentSessionId!);
        var approval = h.Service.SelectedApprovalMode;

        var send = h.Service.SendAsync("Ordinary work");
        await h.Peer.PromptStarted.Task;
        var requests = h.Peer.Requests.Count;
        h.Peer.Update(Notice(severity, "Model rerouted", "Switched to another model for this response."));
        Assert.Equal(2, Notices(session).Length);
        Assert.True(session.IsBusy);
        Assert.Null(h.Service.Blocker);
        Assert.Equal(selection, h.Profile.LoadSelection(h.Service.CurrentSessionId!));
        Assert.Equal(approval, h.Service.SelectedApprovalMode);
        Assert.Equal(requests, h.Peer.Requests.Count);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
        Assert.Empty(h.Peer.Notifications);
        h.Peer.CompleteSilently(); await send;
        h.Peer.Update(Notice(severity, "Idle notice"));
        Assert.Equal(3, Notices(session).Length);
        Assert.False(session.IsBusy);
        Assert.Empty(h.Events.OfType<ChatMessageEvent>());
        Assert.Empty(h.Events.OfType<ChatReasoningDeltaEvent>());
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
    }

    [AvaloniaFact]
    public async Task DismissalIsLocalAndOnlyIdenticalNoticesCoalesceWithinTheBoundedList()
    {
        await using var h = new AcpHarness(); var session = Host(h).CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        await h.Service.RefreshCommandsAsync();
        h.Peer.Update(new { sessionUpdate = "notice", severity = (string?)null, title = "Invalid" });
        h.Peer.Update(new { sessionUpdate = "notice", severity = "info", title = "" });
        h.Peer.Update(new { sessionUpdate = "notice", severity = "info", title = "Invalid", description = 42 });
        h.Peer.Update(new { sessionUpdate = "notice", severity = "info", title = "Invalid", _meta = "invalid" });
        Assert.Empty(Notices(session));
        for (var i = 0; i < 100; i++) h.Peer.Update(Notice());
        h.Peer.Update(new { sessionUpdate = "notice", severity = "info", title = "Runtime notice" });
        Assert.Single(Notices(session));
        h.Peer.Update(Notice(description: "Different details"));
        h.Peer.Update(Notice("warning"));
        h.Peer.Update(new { sessionUpdate = "notice", severity = "info", title = "Runtime notice", _meta = new { source = "first" } });
        h.Peer.Update(new { sessionUpdate = "notice", severity = "info", title = "Runtime notice", _meta = new { source = "second" } });
        Assert.Equal(5, Notices(session).Length);
        var requests = h.Peer.Requests.Count;
        session.Notices.Entries[0].DismissCommand.Execute(null);
        Assert.Equal(4, Notices(session).Length);
        Assert.Equal(requests, h.Peer.Requests.Count);
        Assert.Empty(h.Peer.Notifications);
        h.Peer.Update(Notice()); // A later occurrence remains a new live event.
        Assert.Equal(5, Notices(session).Length);
        for (var i = 0; i < 25; i++) h.Peer.Update(Notice(title: "Notice " + i));
        Assert.Equal(ChatNoticesViewModel.Capacity, Notices(session).Length);
        Assert.Equal(Enumerable.Range(5, 20).Select(x => "Notice " + x), session.Notices.Entries.Select(x => x.Notice.Title));
    }

    [AvaloniaFact]
    public async Task NoticesAreNotHistoryAndCompactionsDoNotCreateAnotherNotice()
    {
        await using var h = new AcpHarness(); var chat = Host(h); var session = chat.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        await h.Service.RefreshCommandsAsync();
        h.Peer.Update(Notice(title: "LIVE_NOTICE_NOT_HISTORY"));
        Assert.Single(Notices(session));
        var send = h.Service.SendAsync("Keep this conversation"); await h.Peer.PromptStarted.Task;
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "compaction", status = "completed" });
        Assert.Single(Notices(session));
        Assert.Single(session.Messages.OfType<ChatMessageCompactionViewModel>());
        h.Peer.Complete(); await send; Dispatcher.UIThread.RunJobs(); chat.SaveState();
        var count = session.Messages.Count;
        foreach (var path in Directory.GetFiles(Path.Combine(h.Workspace, "chat-host"), "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("LIVE_NOTICE_NOT_HISTORY", await File.ReadAllTextAsync(path));
        var id = h.Service.CurrentSessionId!;
        await h.Service.DisposeAsync();
        var peer = new AcpPeer { Id = id, SessionUpdates = [Notice(title: "Fresh after reconnect")] };
        await using var service = new AcpChatService(h.Host, h.Profile, () => h.Agent, () => peer);
        var restoredHost = Host(h, service); var restored = restoredHost.CurrentSession!;
        await AcpHarness.Until(() => restored.IsConnected && peer.Resumes > 0);
        Assert.Equal("Fresh after reconnect", Assert.Single(Notices(restored)).Notice.Title);
        Assert.Equal(count, restored.Messages.Count);
        Assert.Single(restored.Messages.OfType<ChatMessageCompactionViewModel>());
        Assert.Empty(peer.Prompts);
        restoredHost.ShowOverviewCommand.Execute(null);
        Assert.Empty(Notices(restored));
        peer.Update(Notice(title: "After detach"));
        Assert.Empty(Notices(restored));
    }

    [AvaloniaFact]
    public async Task ReconnectNewChatAndStopRejectOldQueuedAndForeignNotices()
    {
        var first = new AcpPeer();
        var second = new AcpPeer { Id = first.Id };
        var third = new AcpPeer();
        var peers = new Queue<AcpPeer>([first, second, third]);
        await using var h = new AcpHarness(factory: peers.Dequeue);
        var session = Host(h).CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected); await h.Service.RefreshCommandsAsync();
        first.Update(Notice(title: "Old connection"));
        Assert.Single(Notices(session));
        first.Update(Notice(title: "Queued before disconnect"));
        first.Drop();
        Assert.Empty(Notices(session));
        await h.Service.RefreshCommandsAsync();
        first.Update(Notice(title: "Late old connection"), second.Id);
        second.Update(Notice(title: "Foreign session"), "foreign");
        Assert.Empty(Notices(session));
        second.Update(Notice(title: "Current connection"));
        Assert.Single(Notices(session));
        await session.NewChatCommand.ExecuteAsync(null);
        Assert.Empty(Notices(session));
        await h.Service.RefreshCommandsAsync();
        second.Update(Notice(title: "Old session"), third.Id);
        Assert.Empty(Notices(session));
        third.Update(Notice(title: "New session"));
        Assert.Single(Notices(session));
        var send = h.Service.SendAsync("Stop fixture"); await third.PromptStarted.Task;
        third.Update(Notice(title: "Queued before stop"));
        await h.Service.AbortAsync(); await send;
        third.Update(Notice(title: "Late after stop"));
        Assert.Empty(Notices(session));
    }

    [AvaloniaFact]
    public async Task AccountIdentityChangesAndSignOutRetireNoticesWithoutChangingTheErrorPath()
    {
        var identity = new Identity(); var peers = new List<NoticeAccountPeer>();
        await using var h = new AcpHarness(factory: () => { var p = new NoticeAccountPeer(identity); peers.Add(p); return p; });
        var session = Host(h).CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected); await h.Service.RefreshCommandsAsync();
        var first = peers[0];
        first.Inner.Update(Notice(title: "First account"));
        Assert.Single(Notices(session));
        first.Inner.Update(Notice(title: "Queued for first account"));
        identity.Email = "second@example.invalid"; first.PushAccount();
        Assert.Empty(Notices(session));
        first.Inner.Update(Notice("error", "Second account advisory"));
        Assert.Single(Notices(session));
        Assert.True(h.Service.IsAuthenticated); Assert.Empty(h.Events.OfType<ChatErrorEvent>());
        await h.Service.SignOutCommand.ExecuteAsync(null);
        Assert.Empty(Notices(session));
        Assert.False(h.Service.IsAuthenticated);
        Assert.Equal("Sign in to Codex", h.Service.Blocker!.Title);
        first.Inner.Update(Notice(title: "Late account"));
        Assert.Empty(Notices(session));
    }

    private sealed class Identity
    {
        public bool SignedIn = true;
        public string Email = "first@example.invalid";
    }

    private sealed class NoticeAccountPeer : IAgentConnection
    {
        private readonly Identity _identity;
        public AcpPeer Inner { get; } = new();
        public NoticeAccountPeer(Identity identity)
        {
            _identity = identity;
            Inner.Notification += (method, value) => Notification?.Invoke(method, value);
        }
        public bool IsConnected => Inner.IsConnected;
        public event Action<string, JsonElement>? Notification;
        public event Action<Exception>? Disconnected { add => Inner.Disconnected += value; remove => Inner.Disconnected -= value; }
        public Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest { get => Inner.ServerRequest; set => Inner.ServerRequest = value; }
        public Task StartAsync(string executable, string directory, CancellationToken token) => Inner.StartAsync(executable, directory, token);
        public Task NotifyAsync(string method, object parameters, CancellationToken token = default) => Inner.NotifyAsync(method, parameters, token);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public void PushAccount() => Notification?.Invoke("_auth/status_update", AcpPeer.Json(new
        {
            authStatus = new { kind = _identity.SignedIn ? "account" : "none", label = "ChatGPT", account = new { email = _identity.Email } }
        }));
        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token = default)
        {
            if (method == "initialize") return Task.FromResult(AcpPeer.Json(new
            {
                protocolVersion = 1, agentCapabilities = new
                {
                    mcpCapabilities = new { http = true }, sessionCapabilities = new { resume = new { } },
                    auth = new { logout = new { } }, _meta = new { authStatus = new { } }
                }
            }));
            if (method == "logout") { _identity.SignedIn = false; PushAccount(); return Task.FromResult(AcpPeer.Json(new { })); }
            if (method == "authentication/status") return Task.FromResult(AcpPeer.Json(new
            {
                type = _identity.SignedIn ? "chat-gpt" : "unauthenticated", email = _identity.SignedIn ? _identity.Email : ""
            }));
            return Inner.RequestAsync(method, parameters, token);
        }
    }

    private static ChatViewModel Host(AcpHarness h, AcpChatService? service = null)
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
        chat.RegisterChatService(service ?? h.Service); chat.InitializeContent();
        if (chat.CurrentSession == null) chat.OpenNewChat();
        return chat;
    }
}
