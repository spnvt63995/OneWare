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

public sealed class AcpCompactionTests
{
    private static object Status(string id, string status) => new { sessionUpdate = "compaction_update", compactionId = id, status };
    private static object Chunk(string id, string text) => new
        { sessionUpdate = "compaction_summary_chunk", compactionId = id, content = new { type = "text", text } };
    private static ChatMessageCompactionViewModel Card(ChatSessionViewModel session)
    {
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(session.Messages.OfType<ChatMessageCompactionViewModel>());
    }

    [AvaloniaTheory]
    [InlineData("Ordinary work")]
    [InlineData("/compact")]
    public async Task AutomaticAndExplicitCompactionUseOneCardWithoutInferringUsageOrCompletion(string command)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = [new { name = "compact", description = "Compact context" }];
        var chat = Host(h); var session = chat.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync(command);
        await h.Peer.PromptStarted.Task;
        var capabilities = h.Peer.Requests.Single(x => x.Method == "initialize").Parameters.GetProperty("clientCapabilities");
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("session").GetProperty("compaction").ValueKind);
        h.Peer.Update(new { sessionUpdate = "usage_update", used = 8000, size = 10000 });
        h.Peer.Update(Status("compact-one", "in_progress"));
        var card = Card(session); var position = session.Messages.IndexOf(card);
        Assert.True(card.IsInProgress);
        Assert.False(card.HasSummary);
        h.Peer.Update(Status("compact-one", "completed"));
        Assert.Same(card, Card(session));
        Assert.Equal("Completed", card.StatusText);
        Assert.Equal(position, session.Messages.IndexOf(card));
        Assert.Equal(8000, h.Service.ContextCurrentTokens);
        h.Peer.Update(new { sessionUpdate = "usage_update", used = 2000, size = 10000 });
        Assert.Equal(2000, h.Service.ContextCurrentTokens);
        h.Peer.CompleteSilently(); await send;
        Assert.False(Card(session).IsInProgress);
        Assert.Empty(h.Events.OfType<ChatMessageEvent>());
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
    }

    [AvaloniaFact]
    public async Task SummaryAndErrorPatchesPreserveReplaceClearAndAppendWithoutInventingUnknownStatus()
    {
        await using var h = new AcpHarness(); var session = Host(h).CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Summary fixture"); await h.Peer.PromptStarted.Task;
        h.Peer.Update(Chunk("one", "First "));
        var card = Card(session);
        Assert.True(card.IsInProgress);
        h.Peer.Update(Chunk("one", "summary"));
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "one", status = "failed", error = "Fixture failure", _meta = new { version = 1 } });
        Assert.Equal("First summary", Card(session).SummaryText);
        Assert.Equal("Failed", card.StatusText);
        Assert.Equal("Fixture failure", card.Error);
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "one", status = "_paused", summary = new[] { new { type = "text", text = "Replacement" } } });
        Assert.Equal("Replacement", Card(session).SummaryText);
        Assert.Equal("Status: _paused", card.StatusText);
        Assert.False(card.IsInProgress);
        Assert.Equal("Fixture failure", card.Error);
        Assert.Equal(1, card.State.Metadata!.Value.GetProperty("version").GetInt32());
        h.Peer.Update(new { sessionUpdate = "compaction_summary_chunk", compactionId = "one", content = new { type = "text", text = " appended" }, _meta = new { version = 2 } });
        Assert.Equal("Replacement appended", Card(session).SummaryText);
        Assert.Equal(1, card.State.Metadata!.Value.GetProperty("version").GetInt32());
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "one", status = "cancelled", summary = (object?)null, error = (string?)null, _meta = (object?)null });
        Assert.False(Card(session).HasSummary); Assert.False(card.HasError); Assert.Null(card.State.Metadata);
        h.Peer.Update(Chunk("one", "Discard me"));
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "one", status = "completed", summary = Array.Empty<object>() });
        Assert.False(Card(session).HasSummary);
        h.Peer.Update(Chunk("one", "Retained"));
        Assert.Equal("Retained", Card(session).SummaryText);
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "one", status = (string?)null, summary = Array.Empty<object>() });
        Assert.Equal("Retained", Card(session).SummaryText);
        h.Peer.CompleteSilently(); await send;
    }

    [AvaloniaFact]
    public async Task SavedCardsKeepTheirPositionAndReplayPatchesTheSameEntity()
    {
        await using var h = new AcpHarness(); var chat = Host(h); var session = chat.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Before compaction"); await h.Peer.PromptStarted.Task;
        h.Peer.Update(new { sessionUpdate = "compaction_update", compactionId = "saved", status = "completed", summary = new[] { new { type = "text", text = "Saved summary" } } });
        var card = Card(session); var position = session.Messages.IndexOf(card);
        h.Peer.Complete(); await send; Dispatcher.UIThread.RunJobs();
        chat.SaveState(); var count = session.Messages.Count; var id = h.Service.CurrentSessionId!;
        await h.Service.DisposeAsync();

        var peer = new AcpPeer { Id = id, SessionUpdates = [Status("saved", "completed"), Chunk("saved", " plus replay")] };
        await using var service = new AcpChatService(h.Host, h.Profile, () => h.Agent, () => peer);
        var reopened = Host(h, service); var restored = reopened.CurrentSession!;
        await AcpHarness.Until(() => restored.IsConnected && peer.Resumes > 0);
        var restoredCard = Card(restored);
        Assert.Equal(count, restored.Messages.Count);
        Assert.Equal(position, restored.Messages.IndexOf(restoredCard));
        Assert.Equal("Saved summary plus replay", restoredCard.SummaryText);
        Assert.Equal("Completed", restoredCard.StatusText);
        peer.Update(Status("saved", "completed"));
        Assert.Same(restoredCard, Card(restored));
        Assert.Equal("Saved summary plus replay", restoredCard.SummaryText);
        Assert.Empty(peer.Prompts);
        reopened.SaveState();
        var history = Assert.Single(Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(history));
        Assert.Contains("Saved summary", saved.RootElement.GetRawText());
    }

    [AvaloniaTheory]
    [InlineData("stop")]
    [InlineData("failure")]
    [InlineData("disconnect")]
    public async Task StopOrFailureInterruptsUnfinishedCardsAndRejectsForeignOrOldConnectionUpdates(string outcome)
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () => { var peer = new AcpPeer(); peers.Add(peer); return peer; });
        var session = Host(h).CurrentSession!; await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Interrupted compaction"); await AcpHarness.Until(() => peers[0].Prompts.Count == 1);
        peers[0].Update(Status("unfinished", "in_progress"));
        var card = Card(session);
        if (outcome == "failure") peers[0].FailResponse(new IOException("Synthetic failure"));
        else if (outcome == "disconnect") peers[0].Drop();
        else await h.Service.AbortAsync();
        await send;
        Assert.Same(card, Card(session)); Assert.True(card.State.IsInterrupted);
        Assert.Equal("in_progress", card.State.Status); Assert.False(card.IsInProgress);
        if (outcome != "stop") Assert.Single(h.Events.OfType<ChatErrorEvent>());
        await session.NewChatCommand.ExecuteAsync(null);
        var next = h.Service.SendAsync("Next session"); await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var count = h.Events.OfType<ChatCompactionEvent>().Count();
        peers[0].Update(Status("late", "completed"), peers[1].Id);
        peers[1].Update(Status("foreign", "completed"), peers[0].Id);
        Assert.Equal(count, h.Events.OfType<ChatCompactionEvent>().Count());
        peers[1].Update(Status("current", "completed"));
        Assert.Equal("current", Card(session).State.Id);
        peers[1].CompleteSilently(); await next;
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
