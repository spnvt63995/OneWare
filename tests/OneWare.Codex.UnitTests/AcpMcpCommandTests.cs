using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OneWare.Codex.Views;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpMcpCommandTests
{
    private static object[] Catalog =>
        [new { name = "mcp", description = "Show MCP servers or reconnect them.", input = new { hint = "[reconnect]" } }];

    [AvaloniaFact]
    public async Task PickerOffersStatusAndReconnectAndRejectsOtherArgumentsBeforeDispatch()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog;
        var view = new AcpCommandsView { DataContext = h.Service };
        var window = new Window { Content = view, Width = 420, Height = 100 };
        window.Show();
        try
        {
            var button = view.FindControl<Button>("CommandsButton")!;
            button.Flyout!.ShowAt(button);
            await AcpHarness.Until(() => h.Service.Commands.Count == 1);
            h.Service.SelectedCommand = Assert.Single(h.Service.Commands);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<TextBox>("CommandArguments")!.IsVisible);
            Assert.Equal("[reconnect]", h.Service.CommandHint);
            Assert.True(h.Service.CanRunCommand);
            Assert.Contains("all open sessions", h.Service.CommandsStatus);
            foreach (var invalid in new[] { "login", "reconnect extra", "reconnect\n/status" })
            {
                h.Service.CommandArguments = invalid;
                Assert.False(h.Service.CanRunCommand);
                await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("/mcp " + invalid));
            }
            Assert.Empty(h.Peer.Prompts);
            h.Service.CommandArguments = " ReConnect ";
            Dispatcher.UIThread.RunJobs();
            Assert.True(h.Service.CanRunCommand);
            view.FindControl<Button>("RunCommandButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(button.Flyout.IsOpen);
            Assert.Equal("/mcp ReConnect", Assert.Single(h.Peer.Prompts).GetProperty("prompt")[0].GetProperty("text").GetString());
            Assert.False(h.Service.CanRunCommand);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("/mcp", ChatSendMode.Send));
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("/mcp reconnect", ChatSendMode.Queue));
            h.Peer.CompleteSilently();
            await AcpHarness.Until(() => h.Events.OfType<ChatIdleEvent>().Any());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("/mcp", "oneware: connected\noptional: disabled\npending: starting")]
    [InlineData("/mcp reconnect", "Reloaded the MCP configuration.\nbroken: failed (connection refused)\nSome MCP servers are still starting.\nlogin: authentication required; this client cannot open a sign-in page.")]
    public async Task RuntimeStatusesArePreservedWithSessionSelectionAndCurrentHostTools(string command, string response)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog;
        await h.Service.RefreshCommandsAsync();
        var session = h.Service.CurrentSessionId;
        var selection = h.Profile.LoadSelection(session!);
        var approval = h.Service.SelectedApprovalMode;
        h.Register("REFRESHED_");
        Assert.True(h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "draft.png"));
        var send = h.Service.SendAsync(command);
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var prompt = Assert.Single(h.Peer.Prompts).GetProperty("prompt");
        Assert.Equal(command, Assert.Single(prompt.EnumerateArray()).GetProperty("text").GetString());
        var toolResult = await h.Peer.CallTool("mcp-current-tools", "acp_fixture", new { value = "value" });
        Assert.Contains("REFRESHED_value", toolResult.GetRawText());
        h.Peer.Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = response } }, "foreign-session");
        Assert.Empty(h.Events.OfType<ChatMessageDeltaEvent>());
        h.Peer.Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = response } });
        h.Peer.CompleteSilently();
        await send;
        Assert.Equal(response, Assert.Single(h.Events.OfType<ChatMessageEvent>()).Content);
        Assert.Equal(session, h.Service.CurrentSessionId);
        Assert.Equal(selection, h.Profile.LoadSelection(session!));
        Assert.Equal(approval, h.Service.SelectedApprovalMode);
        Assert.Single(h.Service.Attachments.Items);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStatusOrTransportFailureDoesNotReportReconnectSuccess(bool transportFailure)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog;
        h.Peer.FailPrompt = transportFailure;
        var send = h.Service.SendAsync("/mcp reconnect");
        if (!transportFailure)
        {
            await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Peer.CompleteSilently();
        }
        await send;
        if (transportFailure)
        {
            Assert.Single(h.Events.OfType<ChatErrorEvent>());
            Assert.Empty(h.Events.OfType<ChatMessageEvent>());
        }
        else Assert.Contains("returned no status", Assert.Single(h.Events.OfType<ChatMessageEvent>()).Content);
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
    }

    [AvaloniaFact]
    public async Task UnadvertisedMcpCannotFallThroughToAnOrdinaryPrompt()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = [];
        await h.Service.SendAsync("/mcp");
        Assert.Empty(h.Peer.Prompts);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task McpDoesNotMakeAConversationWithPreviousWorkRecreatable()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Catalog;
        var send = h.Service.SendAsync("ordinary work");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Complete();
        await send;
        h.Peer.ResetPrompt();
        send = h.Service.SendAsync("/mcp reconnect");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.CompleteSilently();
        await send;
        Assert.False(h.Profile.CanRecreateSession(h.Service.CurrentSessionId!));
    }
}
