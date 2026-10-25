using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpAdapterTests
{
    [AvaloniaFact]
    public async Task FirstPromptIncludesLazilyRegisteredBuiltInsWithoutAFalseRegistryChange()
    {
        await using var h = new AcpHarness(initializeTools: false);
        var send = h.Service.SendAsync("first prompt in a new application");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
        Assert.Contains(h.Host.GetTools(), t => t.Function.Name == "readFile");
        h.Peer.Tool("first");
        Assert.False((await h.Peer.Call("first")).GetProperty("isError").GetBoolean());
        Assert.Single(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAdapterMatchesHttpAndAcpInEitherOrderAndInvokesOriginalRegistryOnce(bool httpFirst)
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("read the fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var call = "call-first";
        Task<JsonElement> result;
        if (httpFirst)
        {
            result = h.Peer.Call(call);
            await Task.Delay(30);
            Assert.Empty(h.Starts);
            Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
            h.Peer.Tool(call);
        }
        else
        {
            h.Peer.Tool(call);
            Assert.Empty(h.Starts);
            Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
            result = h.Peer.Call(call);
        }

        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Empty(h.Starts);
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        var response = await result;
        Assert.False(response.GetProperty("isError").GetBoolean());
        // Preserve the original AiFunctionProvider's JSON serialization, including string quotes.
        Assert.Equal(JsonSerializer.Serialize("ACP_FIXTURE_value"),
            response.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(h.Peer.Id, Assert.Single(h.Starts).SessionId);
        Assert.Equal(call, h.Starts[0].ToolCallId);
        Assert.True((await h.Peer.Call(call)).GetProperty("isError").GetBoolean());
        Assert.Single(h.Starts);
        Assert.DoesNotContain(h.Events, e => e is ChatToolExecutionStartEvent { IsClientTool: false });
        h.Peer.Complete();
        await send;
        Assert.Equal("answer", Assert.Single(h.Events.OfType<ChatMessageEvent>()).Content);
    }

    [AvaloniaTheory]
    [InlineData("foreign-session")]
    [InlineData("arguments")]
    [InlineData("tool")]
    [InlineData("missing-call-id")]
    public async Task IdentityMismatchCannotAskApprovalOrExecute(string mismatch)
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("call", mismatch == "tool" ? "other" : "acp_fixture",
            mismatch == "arguments" ? "different" : "value");
        var result = await h.Peer.Call("call", mismatch == "foreign-session" ? "foreign" : null,
            omitCallId: mismatch == "missing-call-id");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task AbortExpiresPendingApprovalAndTheNextPromptRequiresAnotherDecision()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("old");
        var call = h.Peer.Call("old");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var old = h.Events.OfType<ChatPermissionRequestEvent>().Single();
        await h.Service.AbortAsync();
        await send;
        old.AllowForSessionCommand!.Execute(null);
        // Abort closes the owned HTTP endpoint. Depending on reply/close ordering, the old
        // caller receives a cancellation result or a transport close; neither may execute.
        try
        {
            Assert.True((await call).GetProperty("isError").GetBoolean());
        }
        catch (HttpRequestException)
        {
        }

        Assert.Empty(h.Starts);
        h.Peer.ResetPrompt();
        var next = h.Service.SendAsync("again");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("new");
        var nextCall = h.Peer.Call("new");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Count() == 2);
        h.Events.OfType<ChatPermissionRequestEvent>().Last().DenyCommand.Execute(null);
        Assert.True((await nextCall).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await next;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalIsRequiredEvenForReadOnlyHintAndDenyNeverInvokes(bool allow)
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("approval");
        var call = h.Peer.Call("approval");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var permission = h.Events.OfType<ChatPermissionRequestEvent>().Single();
        (allow ? permission.AllowCommand : permission.DenyCommand).Execute(null);
        Assert.Equal(!allow, (await call).GetProperty("isError").GetBoolean());
        Assert.Equal(allow ? 1 : 0, h.Starts.Count);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadReplaysNeitherMessagesNorToolsAndKeepsTheExactSession(bool supportsResume)
    {
        await using var h = new AcpHarness();
        h.Peer.SupportsResume = supportsResume;
        var send = h.Service.SendAsync("initial");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Complete();
        await send;
        var session = h.Service.CurrentSessionId;
        h.Events.Clear();
        h.Peer.ReplayOnResume = true;
        Assert.True(await h.Service.LoadSessionAsync(session!));
        Assert.Equal(session, h.Service.CurrentSessionId);
        // Resume clears transient notices; replay must not emit any other chat events.
        Assert.All(h.Events, e => Assert.IsType<ChatNoticesClearedEvent>(e));
        Assert.Empty(h.Starts);
        Assert.Equal(1, h.Peer.NewSessions);
        Assert.Equal(1, h.Peer.Resumes);
        Assert.Equal(supportsResume ? "session/resume" : "session/load", h.Peer.LastResumeMethod);
    }

    [AvaloniaFact]
    public async Task AbortDuringInitializationDoesNotStartAPrompt()
    {
        await using var h = new AcpHarness();
        h.Peer.BlockInitialization = true;
        var send = h.Service.SendAsync("cancel before a session exists");
        await h.Peer.Initializing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Service.AbortAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Peer.NewSessions);
        Assert.False(h.Peer.PromptStarted.Task.IsCompleted);
        Assert.Empty(h.Starts);
    }

    [AvaloniaFact]
    public async Task DisconnectExpiresApprovalAndRetainsTheOwnedSession()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("disconnect");
        var call = h.Peer.Call("disconnect");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var permission = h.Events.OfType<ChatPermissionRequestEvent>().Single();
        var id = h.Service.CurrentSessionId;
        h.Peer.Drop();
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        permission.AllowForSessionCommand!.Execute(null);
        Assert.True((await call).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        Assert.Equal(id, h.Service.CurrentSessionId);
    }

    [AvaloniaFact]
    public async Task FailedResumeRetainsIdAndRetryDoesNotCreateAnotherSession()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("initial");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Complete();
        await send;
        var id = h.Service.CurrentSessionId!;
        h.Peer.FailResume = true;
        Assert.False(await h.Service.LoadSessionAsync(id));
        Assert.Equal(id, h.Service.CurrentSessionId);
        h.Peer.FailResume = false;
        Assert.True(await h.Service.LoadSessionAsync(id));
        Assert.Equal(1, h.Peer.NewSessions);
        Assert.Equal(id, h.Service.CurrentSessionId);
    }

    [AvaloniaFact]
    public async Task ForeignProfileSessionCannotBeImported()
    {
        await using var h = new AcpHarness();
        Assert.False(await h.Service.LoadSessionAsync(Guid.NewGuid().ToString()));
        Assert.Equal(0, h.Peer.NewSessions);
        Assert.Equal(0, h.Peer.Resumes);
    }

    [AvaloniaFact]
    public async Task RegistryChangeDuringApprovalCannotExecuteOrGrantTheOldTool()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("call");
        var call = h.Peer.Call("call");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        h.Register("replacement");
        h.Events.OfType<ChatPermissionRequestEvent>().Single().AllowForSessionCommand!.Execute(null);
        Assert.True((await call).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task NativePermissionsAndFileCallbacksCannotBypassOneWare()
    {
        await using var h = new AcpHarness();
        Assert.True(await h.Service.InitializeAsync());
        var answer = AcpPeer.Json(await h.Peer.ServerRequest!("session/request_permission",
            AcpPeer.Json(new { sessionId = h.Peer.Id }), CancellationToken.None));
        Assert.Equal("cancelled", answer.GetProperty("outcome").GetProperty("outcome").GetString());
        await Assert.ThrowsAsync<NotSupportedException>(() => h.Peer.ServerRequest!("fs/write_text_file",
            AcpPeer.Json(new { }), CancellationToken.None));
        Assert.Empty(h.Starts);
    }

    [AvaloniaTheory]
    [InlineData("model")]
    [InlineData("reasoning_effort")]
    [InlineData("mode")]
    public async Task UnsupportedSelectionNeverSendsAPrompt(string missing)
    {
        await using var h = new AcpHarness();
        h.Peer.MissingOption = missing;
        await h.Service.SendAsync("must not send");
        Assert.False(h.Peer.PromptStarted.Task.IsCompleted);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
        Assert.Empty(h.Starts);
    }
}