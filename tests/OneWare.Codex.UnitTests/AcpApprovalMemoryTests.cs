using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Codex.Services;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpApprovalMemoryTests
{
    [AvaloniaTheory]
    [InlineData("disconnect")]
    [InlineData("cancel")]
    [InlineData("tools")]
    public async Task ChatApprovalSurvivesTransportReplacement(string cause)
    {
        await using var h = new AcpHarness(confirm: true);
        var send = await Start(h.Service, h.Peer);
        var call = Call(h.Peer, "remember");
        (await Permission(h.Events, 1)).AllowForSessionCommand!.Execute(null);
        Assert.False(Error(await call));
        if (cause == "cancel") await h.Service.AbortAsync();
        else h.Peer.Complete();
        await send;

        if (cause == "disconnect") h.Peer.Drop();
        else if (cause == "tools")
        {
            AcpToolRefreshTests.Register(h, "unrelated_plugin");
            await AcpHarness.Until(() => h.Peer.Resumes == 1);
        }

        send = await Start(h.Service, h.Peer);
        Assert.False(Error(await Call(h.Peer, "remembered").WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
        Assert.Equal(1, h.Peer.NewSessions);
        Assert.Equal(1, h.Peer.Resumes);
        Assert.Equal(h.Peer.Id, h.Service.CurrentSessionId);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task ReopenedSiblingKeepsOnlyItsConversationAndProviderApprovals()
    {
        var original = new AcpPeer();
        var reopened = new AcpPeer { Id = original.Id };
        var fresh = new AcpPeer();
        var returning = new AcpPeer { Id = original.Id };
        var peers = new Queue<AcpPeer>([original, reopened, fresh, returning]);
        await using var h = new AcpHarness(confirm: true, factory: () => peers.Dequeue());
        var a = (AcpChatService)h.Service.CreateSession();
        var events = new List<ChatEvent>();
        a.EventReceived += (_, e) => events.Add(e);
        var send = await Start(a, original);
        var call = Call(original, "remember");
        (await Permission(events, 1)).AllowForSessionCommand!.Execute(null);
        Assert.False(Error(await call));
        original.Complete();
        await send;
        await a.DisposeAsync();

        await using var b = (AcpChatService)h.Service.CreateSession();
        b.EventReceived += (_, e) => events.Add(e);
        Assert.True(await b.LoadSessionAsync(original.Id));
        send = await Start(b, reopened);
        Assert.False(Error(await Call(reopened, "reopened").WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Single(events.OfType<ChatPermissionRequestEvent>());
        reopened.Complete();
        await send;

        await b.NewChatAsync();
        send = await Start(b, fresh);
        call = Call(fresh, "new-chat");
        (await Permission(events, 2)).DenyCommand.Execute(null);
        Assert.True(Error(await call));
        fresh.Complete();
        await send;

        Assert.True(await b.LoadSessionAsync(original.Id));
        send = await Start(b, returning);
        Assert.False(Error(await Call(returning, "back-to-original").WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(2, events.OfType<ChatPermissionRequestEvent>().Count());
        returning.Complete();
        await send;

        // Even the same profile/session ID on a new provider has no durable approval file.
        var restarted = new AcpPeer { Id = original.Id };
        await using var other = new AcpChatService(h.Host, h.Profile, () => null, () => restarted);
        other.EventReceived += (_, e) => events.Add(e);
        Assert.True(await other.LoadSessionAsync(original.Id));
        send = await Start(other, restarted);
        call = Call(restarted, "new-provider");
        (await Permission(events, 3)).DenyCommand.Execute(null);
        Assert.True(Error(await call));
        restarted.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task OnceDenyAndBypassDoNotBecomeChatApprovals()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = await Start(h.Service, h.Peer);
        var once = Call(h.Peer, "once");
        (await Permission(h.Events, 1)).AllowCommand.Execute(null);
        Assert.False(Error(await once));
        var denied = Call(h.Peer, "denied");
        (await Permission(h.Events, 2)).DenyCommand.Execute(null);
        Assert.True(Error(await denied));
        h.Peer.Complete();
        await send;

        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeBypass;
        send = await Start(h.Service, h.Peer);
        Assert.False(Error(await Call(h.Peer, "bypass").WaitAsync(TimeSpan.FromSeconds(5))));
        h.Peer.Complete();
        await send;
        await h.Service.AbortAsync();
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeDefault;
        send = await Start(h.Service, h.Peer);
        var again = Call(h.Peer, "ask-again");
        (await Permission(h.Events, 3)).DenyCommand.Execute(null);
        Assert.True(Error(await again));
        Assert.Equal(2, h.Starts.Count);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredApprovalCannotBeRememberedByALateClick(bool replacedTool)
    {
        await using var h = new AcpHarness(confirm: true);
        var send = await Start(h.Service, h.Peer);
        var call = Call(h.Peer, "cancelled");
        var pending = await Permission(h.Events, 1);
        if (replacedTool)
        {
            h.Register("REPLACED_");
            pending.AllowForSessionCommand!.Execute(null);
            Assert.True(Error(await call));
            h.Peer.Complete();
            await send;
        }
        else
        {
            await h.Service.AbortAsync();
            await send;
            // Closing the owned HTTP endpoint can race the cancellation response.
            try { Assert.True(Error(await call)); }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { }
            pending.AllowForSessionCommand!.Execute(null);
        }

        send = await Start(h.Service, h.Peer);
        call = Call(h.Peer, "retry");
        (await Permission(h.Events, 2)).DenyCommand.Execute(null);
        Assert.True(Error(await call));
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task RememberedCommandPrefixesRemainNarrowAndCannotOverridePlan()
    {
        await using var h = new AcpHarness();
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "runTerminalCommand", FriendlyName = "Terminal", IsReadOnly = false,
            Description = "A fake terminal that never executes commands.",
            Handler = (Func<string, string>)(command => command),
            ConfirmationCheck = _ => "Allow the fake command?"
        });
        Task<JsonElement> Command(string id, string command) =>
            h.Peer.CallTool(id, "runTerminalCommand", new { command });

        var send = await Start(h.Service, h.Peer);
        var call = Command("remember", "git status --short");
        var permission = await Permission(h.Events, 1);
        Assert.Contains("git status", permission.AllowForSessionButtonText);
        permission.AllowForSessionCommand!.Execute(null);
        Assert.False(Error(await call));
        h.Peer.Complete();
        await send;
        await h.Service.AbortAsync();

        send = await Start(h.Service, h.Peer);
        Assert.False(Error(await Command("same-prefix", "git status --branch").WaitAsync(TimeSpan.FromSeconds(5))));
        call = Command("different-prefix", "git push");
        (await Permission(h.Events, 2)).DenyCommand.Execute(null);
        Assert.True(Error(await call));
        call = Command("complex", "git status $(whoami)");
        permission = await Permission(h.Events, 3);
        Assert.Null(permission.AllowForSessionCommand);
        permission.DenyCommand.Execute(null);
        Assert.True(Error(await call));
        h.Peer.Complete();
        await send;

        h.Agent = new ChatAgentDefinition { Id = "plan", DisplayName = "Plan", TurnMode = ChatAgentTurnMode.Plan };
        send = await Start(h.Service, h.Peer);
        Assert.True(Error(await Command("plan", "git status")));
        Assert.Equal(2, h.Starts.Count);
        Assert.Equal(3, h.Events.OfType<ChatPermissionRequestEvent>().Count());
        h.Peer.Complete();
        await send;
    }

    private static async Task<Task> Start(AcpChatService service, AcpPeer peer)
    {
        var count = peer.Prompts.Count;
        var send = service.SendAsync("Use only the isolated fixture.");
        await AcpHarness.Until(() => peer.Prompts.Count == count + 1);
        return send;
    }

    private static Task<JsonElement> Call(AcpPeer peer, string id) =>
        peer.CallTool(id, "acp_fixture", new { value = id });

    private static async Task<ChatPermissionRequestEvent> Permission(List<ChatEvent> events, int count)
    {
        await AcpHarness.Until(() => events.OfType<ChatPermissionRequestEvent>().Count() == count);
        return events.OfType<ChatPermissionRequestEvent>().Last();
    }

    private static bool Error(JsonElement result) => result.GetProperty("isError").GetBoolean();
}
