using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Chat.Views;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpAutomaticStopTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalStopButtonCancelsAutomaticTurnAndLaterResultsWaitForUser(bool streaming)
    {
        await using var f = new Fixture();
        await AcpHarness.Until(() => f.Session.IsConnected);
        var parent = f.Send("Start background tasks."); await f.Ready(1);
        await Start(f.Peers[0], "first"); await f.Ready(2);
        await Start(f.Peers[0], "second"); await f.Ready(3);
        f.Peers[0].Complete(); await parent; Dispatcher.UIThread.RunJobs();
        Assert.False(f.Session.IsBusy); Assert.False(f.Stop.IsVisible);
        Assert.Empty(f.Harness.Events.OfType<ChatTurnStartedEvent>()); // Background work is not a parent turn.

        f.Session.CurrentMessage = "Unsent draft";
        var attachment = Path.Combine(f.Harness.Workspace, "unsent.txt");
        File.WriteAllText(attachment, "UNSENT_ATTACHMENT");
        f.Harness.Service.Attachments.AddFiles([attachment]);
        f.Peers[1].Complete();
        await AcpHarness.Until(() => f.Peers[0].Prompts.Count == 2);
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Session.IsBusy); Assert.True(f.Stop.IsVisible); Assert.True(f.Stop.IsEnabled);
        Assert.True(f.Session.AbortCommand.CanExecute(null));
        Assert.Equal("Unsent draft", f.Session.CurrentMessage);
        Assert.Single(f.Harness.Service.Attachments.Items);
        Assert.Single(f.Session.Messages.OfType<ChatMessageUserViewModel>());
        Assert.Single(f.Harness.Events.OfType<ChatTurnStartedEvent>());

        if (streaming)
        {
            Chunk(f.Peers[0], "PARTIAL_AUTO_RESPONSE"); Dispatcher.UIThread.RunJobs();
            Assert.Contains(f.Session.Messages.OfType<ChatMessageAssistantViewModel>(),
                x => x.Content == "PARTIAL_AUTO_RESPONSE" && x.IsStreaming);
        }
        var pending = f.Peers[0].CallTool("pending", "acp_fixture", new { value = "blocked" });
        await AcpHarness.Until(() => f.Harness.Events.OfType<ChatPermissionRequestEvent>().Any());
        var permission = Assert.Single(f.Harness.Events.OfType<ChatPermissionRequestEvent>());
        await f.Session.AbortCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        try { await pending; } catch (Exception error) when (error is HttpRequestException or JsonException) { }
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Session.IsBusy); Assert.False(f.Stop.IsVisible);
        Assert.False(f.Session.AbortCommand.CanExecute(null));
        Assert.Contains("session/cancel", f.Peers[0].Notifications);
        Assert.False(f.Peers[0].IsConnected);
        Assert.DoesNotContain(f.Session.Messages, x => x is ChatMessagePermissionRequestViewModel);
        Assert.All(f.Session.Messages.OfType<ChatMessageAssistantViewModel>(), x => Assert.False(x.IsStreaming));
        permission.AllowCommand.Execute(null); Assert.Empty(f.Harness.Starts);
        Chunk(f.Peers[0], "LATE_CANCELLED_TEXT"); f.Peers[0].Complete();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(f.Session.Messages.OfType<ChatMessageAssistantViewModel>(),
            x => x.Content.Contains("LATE_CANCELLED_TEXT"));

        // Stop cancels the automatic parent response, while other background work stays owned.
        Assert.True(f.Peers[2].IsConnected);
        Chunk(f.Peers[2], "SECOND_AFTER_STOP:"); f.Peers[2].Complete();
        await AcpHarness.Until(() => f.Harness.Events.OfType<ChatSubAgentCompletedEvent>().Count() == 2);
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Session.IsBusy); Assert.False(f.Stop.IsVisible);
        Assert.Single(f.Harness.Events.OfType<ChatTurnStartedEvent>());
        Assert.Equal(3, f.Peers.Count);

        f.Next.Enqueue(new AcpPeer { Id = f.Peers[0].Id });
        var resumed = f.Send("Resume after Stop."); await f.Ready(4);
        Assert.Equal(1, f.Peers[3].Resumes);
        Assert.Equal(f.Peers[0].Id, f.Peers[3].Prompts[0].GetProperty("sessionId").GetString());
        Assert.Contains("SECOND_AFTER_STOP:answer", f.Peers[3].Prompts[0].GetRawText());
        f.Peers[3].Complete(); await resumed; Dispatcher.UIThread.RunJobs();
        Assert.False(f.Stop.IsVisible); Assert.False(f.Session.IsBusy);
        Assert.Equal(2, f.Session.Messages.OfType<ChatMessageUserViewModel>().Count());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticCompletionAndFailureBothRestoreIdleWithoutSyntheticUserMessages(bool fail)
    {
        await using var f = new Fixture();
        await AcpHarness.Until(() => f.Session.IsConnected);
        var parent = f.Send("Start one task."); await f.Ready(1);
        await Start(f.Peers[0], "start"); await f.Ready(2);
        f.Peers[0].Complete(); await parent; Dispatcher.UIThread.RunJobs();
        Assert.False(f.Stop.IsVisible);
        f.Peers[0].FailPrompt = fail;
        f.Peers[1].Complete();
        if (!fail)
        {
            await AcpHarness.Until(() => f.Peers[0].Prompts.Count == 2);
            Dispatcher.UIThread.RunJobs();
            Assert.True(f.Session.IsBusy); Assert.True(f.Stop.IsVisible); Assert.True(f.Stop.IsEnabled);
            f.Peers[0].Complete();
        }
        await AcpHarness.Until(() => f.Harness.Events.OfType<ChatIdleEvent>().Count() == 2);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(f.Harness.Events.OfType<ChatTurnStartedEvent>());
        Assert.False(f.Session.IsBusy); Assert.False(f.Stop.IsVisible);
        Assert.Single(f.Session.Messages.OfType<ChatMessageUserViewModel>());
        Assert.Equal(fail ? 1 : 0, f.Session.Messages.OfType<ChatMessageErrorViewModel>().Count());
    }

    private static async Task Start(AcpPeer peer, string call)
    {
        var response = await peer.CallTool(call, AcpPluginCatalog.DelegateTool,
            new { agent = "worker", task = "Read the fixture.", background = true });
        Assert.False(response.GetProperty("isError").GetBoolean());
    }

    private static void Chunk(AcpPeer peer, string text) => peer.Update(new
        { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });

    private sealed class Fixture : IAsyncDisposable
    {
        public List<AcpPeer> Peers { get; } = [];
        public Queue<AcpPeer> Next { get; } = [];
        public AcpHarness Harness { get; }
        public ChatViewModel Chat { get; }
        public ChatSessionViewModel Session { get; }
        public ChatView View { get; }
        public Window Window { get; }
        public Button Stop => Assert.Single(View.GetVisualDescendants().OfType<Button>(),
            x => ReferenceEquals(x.Command, Session.AbortCommand));

        public Fixture()
        {
            Harness = new AcpHarness(confirm: true, factory: () =>
            {
                var p = Next.Count > 0 ? Next.Dequeue() : new AcpPeer(); Peers.Add(p); return p;
            });
            Harness.Provider.RegisterAgent(new OneWareAiAgent { Name = "worker", Description = "Stop fixture.",
                Instructions = "Read only.", Model = "gpt-6-luna", ReasoningEffort = "low" });
            var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
            var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
            var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory"
                ? Path.Combine(Harness.Workspace, "chat-host") : null);
            var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
            var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
            {
                "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
                "get_SelectedAgent" => agent, _ => null
            });
            Chat = new ChatViewModel(Harness.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
                InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
            Chat.RegisterChatService(Harness.Service); Chat.InitializeContent();
            Session = Chat.OpenNewChat()!;
            View = new ChatView { DataContext = Chat };
            Window = new Window { Content = View, Width = 800, Height = 600 }; Window.Show();
        }

        public Task Send(string text)
        {
            Session.CurrentMessage = text;
            return Session.SendCommand.ExecuteAsync(null);
        }
        public Task Ready(int count) => AcpHarness.Until(() => Peers.Count >= count && Peers[count - 1].Prompts.Count == 1);
        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync(); Dispatcher.UIThread.RunJobs(); Window.Close(); View.DataContext = null;
            Chat.SaveState();
        }
    }
}
