using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpLiveApprovalTests
{
    [AvaloniaFact]
    public async Task BusyFooterPersistsChangesAndOnlySubsequentToolsUseTheNewMode()
    {
        using var disk = new AcpTestSupport.Profile();
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(confirm: true, settings: settings);
        var view = Assert.IsType<AcpChatFooterView>(h.Service.FooterUiExtension);
        var window = new Window { Content = view, Width = 360, Height = 120 };
        window.Show();
        try
        {
            var send = h.Service.SendAsync("Change approval mode while working.");
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
            var pending = Tool(h.Peer, "pending");
            await Permissions(h, 1);
            var button = view.FindControl<Button>("ModeButton")!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyEnabled);
            button.Flyout!.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            view.FindControl<ListBox>("ModeList")!.SelectedItem = AcpChatService.ApprovalModeBypass;
            Dispatcher.UIThread.RunJobs();
            button.Flyout.Hide();
            Assert.Equal(AcpChatService.ApprovalModeBypass, h.Service.SelectedApprovalMode);
            Assert.False(pending.IsCompleted);
            Assert.Empty(h.Starts);
            settings.Save(disk.Path);
            var restored = AcpTestSupport.Settings(); restored.Load(disk.Path);
            await using var restart = new AcpHarness(settings: restored);
            Assert.Equal(AcpChatService.ApprovalModeBypass, restart.Service.SelectedApprovalMode);

            h.Events.OfType<ChatPermissionRequestEvent>().Single().DenyCommand.Execute(null);
            Assert.True((await pending).GetProperty("isError").GetBoolean());
            Assert.False((await Tool(h.Peer, "bypassed")).GetProperty("isError").GetBoolean());
            Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
            h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeDefault;
            h.Service.SelectedApprovalMode = "unknown-mode";
            Assert.Equal(AcpChatService.ApprovalModeDefault, h.Service.SelectedApprovalMode);
            var after = Tool(h.Peer, "default-again");
            await Permissions(h, 2);
            Assert.False(after.IsCompleted);
            h.Events.OfType<ChatPermissionRequestEvent>().Last().AllowCommand.Execute(null);
            Assert.False((await after).GetProperty("isError").GetBoolean());
            Assert.Equal(2, h.Starts.Count);
            h.Peer.Complete(); await send;
            Assert.Single(h.Peer.Prompts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task QuestionsReadLiveModeWithoutAnsweringAnAlreadyPendingQuestion()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("Ask questions while the mode changes.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        var pending = Question(h.Peer, "first");
        await Questions(h, 1);
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeAutopilot;
        Assert.False(pending.IsCompleted);
        h.Events.OfType<ChatUserInputRequestEvent>().Single().SubmitCommand.Execute("human answer");
        Assert.Equal("human answer", Payload(await pending).GetProperty("answer").GetString());
        Assert.Contains("user is not available", Payload(await Question(h.Peer, "automatic")).GetProperty("answer").GetString());
        Assert.Single(h.Events.OfType<ChatUserInputRequestEvent>());

        // Native ACP elicitation uses the same live choice, including returning to interactive mode.
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeBypass;
        var native = h.Peer.ServerRequest!("elicitation/create", AcpPeer.Json(new
        {
            sessionId = h.Peer.Id, mode = "form", message = "Native question",
            requestedSchema = new { type = "object", required = new[] { "answer" },
                properties = new { answer = new { type = "string" } } }
        }), CancellationToken.None);
        await Questions(h, 2);
        Assert.False(native.IsCompleted);
        h.Events.OfType<ChatUserInputRequestEvent>().Last().SubmitCommand.Execute("interactive again");
        Assert.Equal("interactive again", AcpPeer.Json(await native).GetProperty("content").GetProperty("answer").GetString());
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeDefault;
        var after = Question(h.Peer, "default"); await Questions(h, 3);
        h.Events.OfType<ChatUserInputRequestEvent>().Last().SubmitCommand.Execute("default answer");
        Assert.Equal("default answer", Payload(await after).GetProperty("answer").GetString());
        h.Peer.Complete(); await send;
    }

    [AvaloniaFact]
    public async Task QueuedAndSteeredMessagesDoNotFreezeTheApprovalMode()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("First.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeAutopilot;
        await h.Service.SendAsync("Queue while autonomous.", ChatSendMode.Queue);
        await h.Service.SendAsync("Steer after changing approval mode.", ChatSendMode.Steer);
        Assert.Contains(h.Peer.Requests, x => x.Method == "_session/steering");
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeDefault;
        h.Peer.Complete();
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        Assert.DoesNotContain("Autopilot is selected", h.Peer.Prompts[1].GetRawText());
        var call = Tool(h.Peer, "queued"); await Permissions(h, 1);
        h.Events.OfType<ChatPermissionRequestEvent>().Single().DenyCommand.Execute(null);
        Assert.True((await call).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        h.Peer.Complete(); await send;
    }

    [AvaloniaFact]
    public async Task BackgroundChildAndAutomaticParentUseLivePolicyWithoutWideningTheChildBudget()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(confirm: true,
            factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        h.Provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "worker", Description = "Approval fixture", Instructions = "Use the fixture.",
            Tools = ["acp_fixture"], Model = "gpt-6-luna", ReasoningEffort = "low"
        });
        var first = h.Service.SendAsync("Start background work.");
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        Payload(await peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool,
            new { agent = "worker", task = "Read the fixture.", background = true }));
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var childPending = Tool(peers[1], "child-pending"); await Permissions(h, 1);
        peers[0].Complete(); await first;
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeBypass;
        Assert.False(childPending.IsCompleted);
        h.Events.OfType<ChatPermissionRequestEvent>().Single().DenyCommand.Execute(null);
        Assert.True((await childPending).GetProperty("isError").GetBoolean());
        Assert.False((await Tool(peers[1], "child-bypassed")).GetProperty("isError").GetBoolean());
        Assert.True((await peers[1].CallTool("outside-budget", "readFile", new { path = "never-read" }))
            .GetProperty("isError").GetBoolean());
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeDefault;
        var childAfter = Tool(peers[1], "child-default"); await Permissions(h, 2);
        h.Events.OfType<ChatPermissionRequestEvent>().Last().DenyCommand.Execute(null);
        Assert.True((await childAfter).GetProperty("isError").GetBoolean());
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeAutopilot;
        peers[1].Complete();
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        Assert.Single(h.Events.OfType<ChatTurnStartedEvent>());
        Assert.False((await Tool(peers[0], "automatic-parent")).GetProperty("isError").GetBoolean());
        Assert.Contains("user is not available", Payload(await Question(peers[0], "auto-question")).GetProperty("answer").GetString());
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeDefault;
        var parentAfter = Tool(peers[0], "parent-default"); await Permissions(h, 3);
        h.Events.OfType<ChatPermissionRequestEvent>().Last().DenyCommand.Execute(null);
        Assert.True((await parentAfter).GetProperty("isError").GetBoolean());
        peers[0].Complete();
        await AcpHarness.Until(() => h.Events.OfType<ChatIdleEvent>().Count() == 2);
        Assert.Equal(2, h.Starts.Count);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    private static Task<JsonElement> Tool(AcpPeer peer, string id) =>
        peer.CallTool(id, "acp_fixture", new { value = id });
    private static Task<JsonElement> Question(AcpPeer peer, string id) =>
        peer.CallTool(id, AcpChatService.QuestionTool, new { question = id, allowFreeform = true });
    private static Task Permissions(AcpHarness h, int count) =>
        AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Count() == count);
    private static Task Questions(AcpHarness h, int count) =>
        AcpHarness.Until(() => h.Events.OfType<ChatUserInputRequestEvent>().Count() == count);
    private static JsonElement Payload(JsonElement result)
    {
        Assert.False(result.GetProperty("isError").GetBoolean());
        return JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }
}
