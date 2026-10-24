using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpExecutionPlanTests
{
    private static object Step(string content, string status = "pending", string priority = "medium") =>
        new { content, status, priority };
    private static object Plan(params object[] entries) => new { sessionUpdate = "plan", entries };
    private static ChatExecutionPlanEvent Last(AcpHarness h) => h.Events.OfType<ChatExecutionPlanEvent>().Last();

    [AvaloniaFact]
    public async Task SnapshotsReplaceStepsAndPreserveLastStateWithoutSavingPlanHistory()
    {
        await using var h = new AcpHarness();
        var (chat, session) = Host(h);
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Execution plan fixture");
        await h.Peer.PromptStarted.Task;
        h.Peer.Update(Plan(Step("Inspect workspace"), Step("Update implementation")));
        Dispatcher.UIThread.RunJobs();
        var plan = session.ExecutionPlan;
        Assert.Equal(2, plan.Entries.Count);
        Assert.All(plan.Entries, x => Assert.Equal("Pending", x.StatusText));
        var run = plan.RunId;
        h.Peer.Update(Plan(Step("Inspect workspace", "completed", "high"),
            Step("Update implementation", "in_progress"), Step("Verify changes", "pending", "low")));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1/3 completed · Running", plan.Summary);
        Assert.Equal("High priority", plan.Entries[0].Priority);
        Assert.True(plan.Entries[1].IsInProgress);
        plan.IsExpanded = false;
        h.Peer.Update(Plan(Step("Verify changes", "in_progress"), Step("Inspect workspace", "completed")));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "Verify changes", "Inspect workspace" }, plan.Entries.Select(x => x.Content));
        Assert.False(plan.IsExpanded);
        h.Peer.Update(Plan());
        Dispatcher.UIThread.RunJobs();
        Assert.False(plan.HasEntries);
        h.Peer.Update(Plan(Step("Verify changes", "completed")));
        h.Peer.Complete();
        await send;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(run, plan.RunId);
        Assert.Equal("1/1 completed · Turn finished", plan.Summary);
        Assert.Equal("Completed", Assert.Single(plan.Entries).StatusText);
        var count = h.Events.Count;
        h.Peer.Update(Plan(Step("LATE_PLAN")));
        Assert.Equal(count, h.Events.Count);
        chat.SaveState();
        var history = Assert.Single(Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories));
        Assert.DoesNotContain("Verify changes", await File.ReadAllTextAsync(history));

        var next = h.Service.SendAsync("Next turn without a plan");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        Dispatcher.UIThread.RunJobs();
        Assert.False(plan.HasEntries);
        Assert.NotEqual(run, plan.RunId);
        h.Peer.Complete(); await next;
    }

    [AvaloniaFact]
    public async Task StopAndSessionSwitchRejectLateAndForeignPlansWithoutCompletingSteps()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () => { var peer = new AcpPeer(); peers.Add(peer); return peer; });
        var (_, session) = Host(h);
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("First plan");
        await AcpHarness.Until(() => peers[0].Prompts.Count == 1);
        peers[0].Update(Plan(Step("Interrupted step", "in_progress")));
        await h.Service.AbortAsync(); await send;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ChatPlanRunState.Stopped, session.ExecutionPlan.State);
        var step = Assert.Single(session.ExecutionPlan.Entries);
        Assert.Equal(ChatPlanStepStatus.InProgress, step.Status);
        Assert.Equal("Incomplete", step.StatusText);
        Assert.False(step.IsInProgress);
        await session.NewChatCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.ExecutionPlan.HasEntries);
        var next = h.Service.SendAsync("Second plan");
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var count = h.Events.Count;
        peers[0].Update(Plan(Step("OLD_CONNECTION")), peers[1].Id);
        peers[1].Update(Plan(Step("FOREIGN_SESSION")), peers[0].Id);
        Assert.Equal(count, h.Events.Count);
        peers[1].Update(Plan(Step("Current step", "in_progress")));
        peers[1].Complete(); await next;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Current step", Assert.Single(session.ExecutionPlan.Entries).Content);
        Assert.Equal("Incomplete", session.ExecutionPlan.Entries[0].StatusText);
        Assert.Equal(ChatPlanRunState.Finished, session.ExecutionPlan.State);
    }

    [AvaloniaFact]
    public async Task FailureRetainsUnfinishedStepsAndMalformedSnapshotsDoNotEraseThem()
    {
        await using var h = new AcpHarness();
        var (_, session) = Host(h);
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Failure fixture");
        await h.Peer.PromptStarted.Task;
        h.Peer.Update(Plan(Step("Unfinished step", "in_progress")));
        var valid = Last(h);
        h.Peer.Update(new { sessionUpdate = "plan", entries = (object?)null });
        h.Peer.Update(Plan(Step("Invalid status", "unknown")));
        h.Peer.Update(Plan(Step("Invalid priority", priority: "unknown")));
        h.Peer.Update(Plan(new { status = "pending", priority = "low" }));
        Assert.Same(valid, Last(h));
        h.Peer.FailResponse(new IOException("Synthetic execution failure"));
        await send;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ChatPlanRunState.Failed, session.ExecutionPlan.State);
        Assert.Equal("Incomplete", Assert.Single(session.ExecutionPlan.Entries).StatusText);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task ChildPlanIsIgnoredAndAutomaticParentPlanRemainsStoppable()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () => { var peer = new AcpPeer(); peers.Add(peer); return peer; });
        h.Provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "inspector", DisplayName = "Inspector", Description = "Synthetic plan fixture.",
            Instructions = "Inspect the fixture.", Model = "gpt-6-luna", ReasoningEffort = "low"
        });
        var (_, session) = Host(h);
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Delegate an inspection");
        await AcpHarness.Until(() => peers[0].Prompts.Count == 1);
        var delegated = peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool,
            new { agent = "inspector", task = "Inspect the fixture", background = true });
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        await delegated;
        peers[0].Update(Plan(Step("Parent step", "completed")));
        peers[0].Complete(); await send;
        var count = h.Events.OfType<ChatExecutionPlanEvent>().Count();
        peers[1].Update(Plan(Step("CHILD_ONLY_PLAN", "in_progress")));
        Assert.Equal(count, h.Events.OfType<ChatExecutionPlanEvent>().Count());
        peers[1].Complete();
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        peers[0].Update(Plan(Step("Summarize inspection", "in_progress")));
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.IsBusy);
        Assert.True(session.AbortCommand.CanExecute(null));
        Assert.Equal("Summarize inspection", Assert.Single(session.ExecutionPlan.Entries).Content);
        await session.AbortCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsBusy);
        Assert.Equal(ChatPlanRunState.Stopped, session.ExecutionPlan.State);
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
