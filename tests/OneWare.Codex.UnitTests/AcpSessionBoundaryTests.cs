using System.Collections.ObjectModel;
using System.Text.Json;
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

public sealed class AcpSessionBoundaryTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreKeepsSavedMessagesRejectsReplayCallbacksAndAllowsRepeatedLiveText(bool supportsResume)
    {
        await using var h = new AcpHarness(confirm: true);
        var (id, history, saved) = await SaveConversation(h);
        await h.Service.DisposeAsync();
        var peer = new AcpPeer { Id = id, SupportsResume = supportsResume, ReplayOnResume = true };
        JsonElement? callback = null;
        JsonElement? permission = null;
        peer.OnSessionRequest = async () =>
        {
            callback = await peer.Call("replayed");
            permission = AcpPeer.Json(await peer.ServerRequest!("session/request_permission",
                AcpPeer.Json(new { sessionId = id }), CancellationToken.None));
        };
        await using var service = new AcpChatService(h.Host, h.Profile, () => h.Agent, () => peer);
        var events = new List<ChatEvent>();
        service.EventReceived += (_, e) => events.Add(e);
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.IsConnected && permission.HasValue) resumed.TrySetResult(true);
        };
        var reopened = Host(h, service);
        var restored = reopened.CurrentSession!;
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AcpHarness.Until(() => restored.IsConnected && peer.Resumes == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(supportsResume ? "session/resume" : "session/load", peer.LastResumeMethod);
        Assert.Equal(id, service.CurrentSessionId);
        Assert.Equal(0, peer.NewSessions);
        Assert.Empty(peer.Prompts);
        Assert.True(callback!.Value.GetProperty("isError").GetBoolean());
        Assert.Equal("cancelled", permission!.Value.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.All(events, e => Assert.IsType<ChatNoticesClearedEvent>(e));
        Assert.Empty(h.Starts);
        reopened.SaveState();
        Assert.True(JsonElement.DeepEquals(saved, ReadMessages(history)));

        // Equal content in a new turn is a new message, not a replay identifier.
        restored.CurrentMessage = "same text";
        var followup = restored.SendCommand.ExecuteAsync(null);
        await peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        peer.Complete();
        await followup;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "same text", "same text" },
            restored.Messages.OfType<ChatMessageUserViewModel>().Select(x => x.Message));
        var answers = restored.Messages.OfType<ChatMessageAssistantViewModel>().ToArray();
        Assert.Equal(new[] { "answer", "answer" }, answers.Select(x => x.Content));
        Assert.Equal(2, answers.Select(x => x.MessageId).Distinct().Count());
        reopened.SaveState();
        var updated = ReadMessages(history);
        Assert.Equal(4, updated.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(saved[0], updated[0]));
        Assert.True(JsonElement.DeepEquals(saved[1], updated[1]));
    }

    [AvaloniaTheory]
    [InlineData("transport")]
    [InlineData("different-session")]
    [InlineData("different-workspace")]
    public async Task FailedRestorePreservesTheLocalConversationAndRetriesItsOriginalIdentity(string failure)
    {
        await using var h = new AcpHarness();
        var (id, history, saved) = await SaveConversation(h);
        var selection = h.Profile.LoadSelection(id);
        await h.Service.DisposeAsync();
        if (failure == "different-workspace")
        {
            h.CurrentWorkspace = Path.Combine(h.Workspace, "other");
            Directory.CreateDirectory(h.CurrentWorkspace);
        }
        var failed = new AcpPeer
        {
            Id = failure == "different-session" ? Guid.NewGuid().ToString() : id,
            ResumeError = failure == "transport" ? new IOException("fixture transport failure") : null
        };
        var next = failed;
        await using var service = new AcpChatService(h.Host, h.Profile, () => h.Agent, () => next);
        var errors = new List<ChatErrorEvent>();
        service.EventReceived += (_, e) => { if (e is ChatErrorEvent error) errors.Add(error); };
        var reopened = Host(h, service);
        var restored = reopened.CurrentSession!;
        await AcpHarness.Until(() => errors.Count > 0 && !restored.IsConnecting);
        Dispatcher.UIThread.RunJobs();
        Assert.False(restored.IsConnected);
        Assert.Equal(id, service.CurrentSessionId);
        Assert.Equal(id, h.Profile.LoadRuntimeSession(id));
        Assert.Equal(h.Workspace, h.Profile.LoadBinding(id));
        Assert.Equal(selection, h.Profile.LoadSelection(id));
        Assert.Equal(failure == "different-workspace" ? 0 : 1, failed.Resumes);
        Assert.Equal(0, failed.NewSessions);
        Assert.Empty(failed.Prompts);
        reopened.SaveState();
        var afterFailure = ReadMessages(history);
        // An error card may be added; the original messages must remain intact.
        Assert.True(afterFailure.GetArrayLength() >= saved.GetArrayLength());
        for (var i = 0; i < saved.GetArrayLength(); i++)
            Assert.True(JsonElement.DeepEquals(saved[i], afterFailure[i]));

        h.CurrentWorkspace = h.Workspace;
        next = new AcpPeer { Id = id };
        Assert.True(await service.LoadSessionAsync(id));
        await AcpHarness.Until(() => restored.IsConnected);
        Assert.Equal(id, service.CurrentSessionId);
        Assert.Equal(1, next.Resumes);
        Assert.Equal(0, next.NewSessions);
        Assert.Empty(next.Prompts);
        Assert.Equal("same text", Assert.Single(restored.Messages.OfType<ChatMessageUserViewModel>()).Message);
        Assert.Equal("answer", Assert.Single(restored.Messages.OfType<ChatMessageAssistantViewModel>()).Content);
    }

    [AvaloniaFact]
    public async Task WorkspaceChangeRejectsSteeringAndQueuedInputWhileReturningItsAttachmentsToTheDraft()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("active");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var id = h.Service.CurrentSessionId;
        var file = Path.Combine(h.Workspace, "queued.txt");
        await File.WriteAllTextAsync(file, "QUEUED_CONTENT");
        h.Service.Attachments.AddFiles([file]);
        await h.Service.SendAsync("waiting", ChatSendMode.Queue);
        Assert.Empty(h.Service.Attachments.Items);
        h.CurrentWorkspace = Path.Combine(h.Workspace, "other");
        Directory.CreateDirectory(h.CurrentWorkspace);
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "draft.png");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("steer", ChatSendMode.Steer));
        Assert.DoesNotContain(h.Peer.Requests, x => x.Method == "_session/steering");
        Assert.Equal("draft.png", Assert.Single(h.Service.Attachments.Items).DisplayName);

        h.Peer.Complete();
        await send;
        Assert.Single(h.Peer.Prompts);
        Assert.Contains(h.Events.OfType<ChatErrorEvent>(), e => e.Message?.Contains("workspace changed", StringComparison.Ordinal) == true);
        Assert.Equal(new[] { "active" }, h.Events.OfType<ChatUserMessageEvent>().Select(x => x.Content));
        Assert.Equal(new[] { "draft.png", "queued.txt" },
            h.Service.Attachments.Items.Select(x => x.DisplayName).OrderBy(x => x));
        Assert.False(await h.Service.RemoveMostRecentQueuedMessageAsync());
        Assert.Equal(id, h.Service.CurrentSessionId);

        h.CurrentWorkspace = h.Workspace;
        h.Peer.ResetPrompt();
        var retry = h.Service.SendAsync("retry");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, h.Peer.Prompts.Count);
        Assert.Contains("QUEUED_CONTENT", h.Peer.Prompts[1].GetRawText());
        Assert.Contains("\"type\":\"image\"", h.Peer.Prompts[1].GetRawText());
        h.Peer.Complete();
        await retry;
        Assert.Empty(h.Service.Attachments.Items);
        Assert.Equal(id, h.Service.CurrentSessionId);
    }

    private static async Task<(string Id, string History, JsonElement Messages)> SaveConversation(AcpHarness h)
    {
        var chat = Host(h);
        var session = chat.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "same text";
        var send = session.SendCommand.ExecuteAsync(null);
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Complete();
        await send;
        Dispatcher.UIThread.RunJobs();
        chat.SaveState();
        var history = Assert.Single(Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories));
        var messages = ReadMessages(history);
        Assert.Equal(2, messages.GetArrayLength());
        return (h.Service.CurrentSessionId!, history, messages);
    }

    private static JsonElement ReadMessages(string path)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        return saved.RootElement.GetProperty("Messages").Clone();
    }

    private static ChatViewModel Host(AcpHarness h, AcpChatService? service = null)
    {
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
        var paths = InterfaceStub.Create<IPaths>((name, _) =>
            name == "get_AppDataDirectory" ? Path.Combine(h.Workspace, "chat-host") : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
            "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        chat.RegisterChatService(service ?? h.Service);
        chat.InitializeContent();
        if (chat.CurrentSession == null) chat.OpenNewChat();
        return chat;
    }
}
