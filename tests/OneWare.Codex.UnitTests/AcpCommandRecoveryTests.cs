using Avalonia.Headless.XUnit;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpCommandRecoveryTests
{
    private static object[] Commands =>
        [new { name = "status", description = "Session status" }, new { name = "mcp", description = "MCP servers" }];

    private static AcpRpcException Missing(string id, bool raw = false) => new(AcpPeer.Json(raw
        ? (object)new { code = -32603, message = "Internal error", data = new { details = "no rollout found for thread id " + id } }
        : new { code = -32002, message = "Session not found: " + id, data = new { sessionId = id } }));

    private static AcpChatService Reopen(AcpHarness h, AcpPeer peer, List<ChatEvent>? events = null)
    {
        var service = new AcpChatService(h.Host, h.Profile, () => null, () => peer);
        if (events != null) service.EventReceived += (_, e) => events.Add(e);
        return service;
    }

    private static async Task Status(AcpChatService service, AcpPeer peer, string command = "/status")
    {
        var send = service.SendAsync(command);
        await peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false, "/status")]
    [InlineData(true, "/status")]
    [InlineData(false, "/mcp")]
    [InlineData(true, "/mcp reconnect")]
    public async Task CommandOnlyRestartsKeepTheHistoryKeyAndSelectionWithoutReplayingPrompts(bool rawError, string command)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Commands;
        await Status(h.Service, h.Peer, command);
        var id = h.Service.CurrentSessionId!;
        var selection = h.Profile.LoadSelection(id);
        await h.Service.DisposeAsync();

        var second = new AcpPeer { ResumeError = Missing(id, rawError), AdvertisedCommands = Commands };
        var events = new List<ChatEvent>();
        await using (var reopened = Reopen(h, second, events))
        {
            Assert.True(await reopened.LoadSessionAsync(id));
            Assert.Equal(id, reopened.CurrentSessionId);
            Assert.Equal(second.Id, h.Profile.LoadRuntimeSession(id));
            Assert.Equal(selection, h.Profile.LoadSelection(id));
            Assert.Empty(second.Prompts);
            Assert.Empty(events);
            Assert.Contains("saved messages were kept", reopened.CommandsStatus);
            await Status(reopened, second);
            Assert.Equal(second.Id, second.Prompts.Single().GetProperty("sessionId").GetString());
        }

        var third = new AcpPeer { ResumeError = Missing(second.Id, rawError), AdvertisedCommands = Commands };
        await using var again = Reopen(h, third);
        Assert.True(await again.LoadSessionAsync(id));
        Assert.Equal(id, again.CurrentSessionId);
        Assert.Equal(third.Id, h.Profile.LoadRuntimeSession(id));
        Assert.Equal(h.Workspace, h.Profile.LoadBinding(id));
        Assert.Empty(third.Prompts);
        var updated = new AcpSelection("gpt-6-luna", "high");
        h.Profile.SaveSelection(third.Id, updated);
        Assert.Equal(updated, h.Profile.LoadSelection(id));
        Assert.True(h.Profile.CanRecreateSession(id));
        h.Profile.RequireSessionResume(third.Id);
        h.Profile.SaveSelection(id, selection!);
        Assert.False(h.Profile.CanRecreateSession(id));
        Assert.Equal(third.Id, h.Profile.LoadRuntimeSession(id));
    }

    [AvaloniaTheory]
    [InlineData("ordinary message")]
    [InlineData("/review")]
    [InlineData("/compact")]
    public async Task DispatchedOrCancelledWorkMustResumeAndCannotBeReplaced(string input)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = [new { name = "review", description = "Review" }, new { name = "compact", description = "Compact" }];
        var send = h.Service.SendAsync(input);
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var id = h.Service.CurrentSessionId!;
        Assert.False(h.Profile.CanRecreateSession(id));
        await h.Service.AbortAsync();
        await send;
        await h.Service.DisposeAsync();

        var peer = new AcpPeer { ResumeError = Missing(id), AdvertisedCommands = Commands };
        await using var reopened = Reopen(h, peer);
        Assert.False(await reopened.LoadSessionAsync(id));
        await reopened.RefreshCommandsAsync();
        Assert.Equal(0, peer.NewSessions);
        Assert.Empty(peer.Prompts);
        Assert.Equal(id, reopened.CurrentSessionId);
        Assert.Equal(id, h.Profile.LoadRuntimeSession(id));
        Assert.Contains("Start a new chat", reopened.CommandsStatus);
        Assert.False(reopened.CanRunCommand);
    }

    [AvaloniaFact]
    public async Task LegacyBindingsWithoutCommandOnlyEvidenceAreKeptAndNeverRecreated()
    {
        await using var h = new AcpHarness();
        var id = Guid.NewGuid().ToString();
        h.Profile.SaveBinding(id, h.Workspace);
        var path = Path.Combine(h.Profile.Bindings, id + ".json");
        await File.WriteAllTextAsync(path, AcpPeer.Json(new
        {
            Provider = "Codex ACP", SessionId = id, Directory = h.Workspace
        }).GetRawText());
        var original = await File.ReadAllBytesAsync(path);
        var peer = new AcpPeer { ResumeError = Missing(id), AdvertisedCommands = Commands };
        await using var reopened = Reopen(h, peer);
        Assert.False(await reopened.LoadSessionAsync(id));
        Assert.Equal(0, peer.NewSessions);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(id, reopened.CurrentSessionId);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportOrUnrelatedMissingErrorsDoNotReplaceEvenEmptySessions(bool unrelatedMissing)
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Commands;
        await h.Service.RefreshCommandsAsync();
        var id = h.Service.CurrentSessionId!;
        await h.Service.DisposeAsync();
        var peer = new AcpPeer
        {
            ResumeError = unrelatedMissing ? Missing(Guid.NewGuid().ToString()) : new IOException("connection unavailable"),
            AdvertisedCommands = Commands
        };
        await using var reopened = Reopen(h, peer);
        Assert.False(await reopened.LoadSessionAsync(id));
        await reopened.RefreshCommandsAsync();
        Assert.Equal(0, peer.NewSessions);
        Assert.Empty(reopened.Commands);
        Assert.Contains("Check the runtime and account", reopened.CommandsStatus);
        Assert.DoesNotContain("model options", reopened.CommandsStatus);
    }

    [AvaloniaFact]
    public async Task FailedReplacementKeepsTheBindingAndMenuRetryCanRecoverWithoutInference()
    {
        await using var h = new AcpHarness();
        h.Peer.AdvertisedCommands = Commands;
        await h.Service.RefreshCommandsAsync();
        var id = h.Service.CurrentSessionId!;
        await h.Service.DisposeAsync();
        var path = Path.Combine(h.Profile.Bindings, id + ".json");
        var original = await File.ReadAllBytesAsync(path);
        var peer = new AcpPeer { ResumeError = Missing(id), NewSessionError = new IOException("offline"), AdvertisedCommands = Commands };
        await using var reopened = Reopen(h, peer);
        Assert.False(await reopened.LoadSessionAsync(id));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(id, reopened.CurrentSessionId);
        peer.NewSessionError = null;
        await reopened.RefreshCommandsAsync();
        Assert.Equal(peer.Id, h.Profile.LoadRuntimeSession(id));
        Assert.Equal(id, reopened.CurrentSessionId);
        Assert.Equal(2, reopened.Commands.Count);
        Assert.Empty(peer.Prompts);
        Assert.Contains("Reconnected", reopened.CommandsStatus);
    }
}
