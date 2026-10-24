using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpCommandTests
{
    private static object[] Catalog(params string[] names) => names.Select(name => (object)new
    {
        name, description = "Fixture " + name,
        input = name.StartsWith("review") ? new { hint = "Review target" } : null
    }).ToArray();

    private static void Advertise(AcpPeer peer, params string[] names) => peer.Update(new
        { sessionUpdate = "available_commands_update", availableCommands = Catalog(names) });

    [AvaloniaFact]
    public async Task CatalogBindsEarlyAnnouncementsAndReplacesIdleUpdatesWithoutCrossingSessions()
    {
        var first = new AcpPeer { AdvertisedCommands = Catalog("status", "review", "logout") };
        var resumed = new AcpPeer { Id = first.Id, AdvertisedCommands = Catalog("compact") };
        var fresh = new AcpPeer { AdvertisedCommands = Catalog("skills") };
        var peers = new Queue<AcpPeer>([first, resumed, fresh]);
        await using var h = new AcpHarness(factory: peers.Dequeue);
        await h.Service.RefreshCommandsAsync();
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
        Assert.Equal(new[] { "status", "review", "logout" }, h.Service.Commands.Select(x => x.Name));
        Assert.False(h.Service.Commands.Single(x => x.Name == "logout").IsSupported);
        h.Service.SelectedCommand = h.Service.Commands.Single(x => x.Name == "review");
        h.Service.CommandArguments = "pending input";
        Advertise(first, "status");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("status", Assert.Single(h.Service.Commands).Name);
        Assert.Null(h.Service.SelectedCommand);
        Assert.Empty(h.Service.CommandArguments);
        first.Update(new { sessionUpdate = "available_commands_update", availableCommands = Catalog("review") }, "foreign");
        Assert.Equal("status", Assert.Single(h.Service.Commands).Name);

        first.Drop();
        Assert.Empty(h.Service.Commands);
        await h.Service.RefreshCommandsAsync();
        Assert.Equal(1, resumed.Resumes);
        Advertise(first, "review");
        Assert.Equal("compact", Assert.Single(h.Service.Commands).Name);
        await h.Service.NewChatAsync();
        Assert.Empty(h.Service.Commands);
        Advertise(resumed, "logout");
        Assert.Empty(h.Service.Commands);
        await h.Service.RefreshCommandsAsync();
        Assert.Equal("skills", Assert.Single(h.Service.Commands).Name);
    }

    [AvaloniaFact]
    public async Task FirstTypedCommandWaitsForTheCatalogAfterTheSessionReply()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("/status");
        await AcpHarness.Until(() => h.Service.CurrentSessionId != null);
        Assert.Empty(h.Peer.Prompts);
        Advertise(h.Peer, "status");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Complete();
        await send;
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaTheory]
    [InlineData("/status")]
    [InlineData("/skills")]
    [InlineData("/compact")]
    [InlineData("/review")]
    [InlineData("/review focus on error handling")]
    [InlineData("/review-branch main")]
    [InlineData("/review-commit abc123")]
    public async Task CommandsUseAnUnwrappedPromptAndFinishEvenWithoutText(string command)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog("status", "skills", "compact", "review", "review-branch", "review-commit");
        var send = h.Service.SendAsync(command);
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var block = Assert.Single(h.Peer.Prompts.Single().GetProperty("prompt").EnumerateArray());
        Assert.Equal(command, block.GetProperty("text").GetString());
        Assert.Single(h.Events.OfType<ChatTurnStartedEvent>());
        h.Peer.CompleteSilently();
        await send;
        Assert.Single(h.Events.OfType<ChatMessageEvent>());
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task CommandsPreserveAttachmentsAndRejectSteeringWhileOrdinaryQueueKeepsItsPayload()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog("compact", "status");
        Assert.True(h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "draft.png"));
        var send = h.Service.SendAsync("/compact");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(h.Service.Attachments.Items);
        Assert.Single(h.Peer.Prompts[0].GetProperty("prompt").EnumerateArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("/status", ChatSendMode.Queue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("/status", ChatSendMode.Steer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("ordinary steering", ChatSendMode.Steer));
        Assert.DoesNotContain(h.Peer.Requests, x => x.Method == "_session/steering");
        await h.Service.SendAsync("queued message", ChatSendMode.Queue);
        Assert.Empty(h.Service.Attachments.Items);
        h.Peer.CompleteSilently();
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        var prompt = h.Peer.Prompts[1].GetProperty("prompt");
        Assert.Contains("User request:\nqueued message", prompt[0].GetProperty("text").GetString());
        Assert.Equal("image", prompt[1].GetProperty("type").GetString());
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task UnsupportedOrRemovedCommandsNeverFallThroughToModelPrompts()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog("status", "logout", "plan", "rename", "mcp");
        await h.Service.RefreshCommandsAsync();
        foreach (var text in new[] { "/logout", "/plan", "/rename title", "/mcp login", "/mcp reconnect extra", "/review-branch", "/status extra" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync(text));
        Advertise(h.Peer);
        await h.Service.SendAsync("/status");
        Assert.Empty(h.Peer.Prompts);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
        Assert.DoesNotContain(h.Peer.Requests, x => x.Method.StartsWith("_auth/"));
    }

    [AvaloniaTheory]
    [InlineData("/review")]
    [InlineData("/mcp reconnect")]
    public async Task CommandsCannotInvokeWritesOrLiftThePlanRestriction(string command)
    {
        await using var h = new AcpHarness();
        h.Agent = new ChatAgentDefinition { Id = "plan", DisplayName = "Plan", TurnMode = ChatAgentTurnMode.Plan };
        h.Peer.AdvertisedCommands = Catalog("review", "mcp");
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "acp_fixture", Description = "Writable fixture", IsReadOnly = false,
            Handler = (Func<string, string>)(value => "wrote " + value)
        });
        var send = h.Service.SendAsync(command);
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await h.Peer.CallTool("write", "acp_fixture", new { value = "value" })).GetProperty("isError").GetBoolean());
        var permission = await h.Peer.ServerRequest!("session/request_permission", AcpPeer.Json(new
        {
            sessionId = h.Peer.Id,
            toolCall = new { toolCallId = "plan-review:command", kind = "switch_mode", status = "pending", rawInput = new { plan = "Edit files" } },
            options = new[]
            {
                new { optionId = "implement_plan", kind = "allow_once" },
                new { optionId = "revise_plan", kind = "reject_once" }
            }
        }), default);
        Assert.Equal("cancelled", AcpPeer.Json(permission).GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Empty(h.Events.OfType<ChatPlanReadyEvent>());
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData("/compact")]
    [InlineData("/mcp reconnect")]
    public async Task StopCancelsTheCommandAndRejectsLateOutputAndCatalogUpdates(string command)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog("compact", "mcp");
        var send = h.Service.SendAsync(command);
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Service.AbortAsync();
        await send;
        Assert.Contains("session/cancel", h.Peer.Notifications);
        var count = h.Events.Count;
        h.Peer.Complete();
        Advertise(h.Peer, "status");
        Assert.Equal(count, h.Events.Count);
        Assert.Empty(h.Service.Commands);
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
    }

    [AvaloniaFact]
    public async Task PickerSearchArgumentsAndKeyboardSelectionDoNotSendUntilRun()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog("status", "review-branch", "plan");
        var view = new AcpCommandsView { DataContext = h.Service };
        var window = new Window { Content = view, Width = 420, Height = 100 };
        window.Show();
        try
        {
            var button = view.FindControl<Button>("CommandsButton")!;
            button.Flyout!.ShowAt(button);
            await AcpHarness.Until(() => h.Service.Commands.Count == 3);
            h.Service.CommandSearchText = "review";
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("CommandList")!;
            Assert.Equal(1, list.ItemCount);
            var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down };
            view.FindControl<TextBox>("CommandSearch")!.RaiseEvent(down);
            Assert.True(down.Handled);
            Assert.Equal("review-branch", h.Service.SelectedCommand?.Name);
            Assert.False(h.Service.CanRunCommand);
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            list.RaiseEvent(enter);
            Assert.True(enter.Handled);
            Assert.Empty(h.Peer.Prompts);
            h.Service.CommandArguments = "main";
            Dispatcher.UIThread.RunJobs();
            Assert.True(h.Service.CanRunCommand);
            view.FindControl<Button>("RunCommandButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(button.Flyout.IsOpen);
            Assert.Equal("/review-branch main", h.Peer.Prompts.Single().GetProperty("prompt")[0].GetProperty("text").GetString());
            h.Peer.Complete();
            await AcpHarness.Until(() => h.Events.OfType<ChatIdleEvent>().Any());
        }
        finally { window.Close(); }
    }
}
