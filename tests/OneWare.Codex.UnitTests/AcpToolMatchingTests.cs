using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpToolMatchingTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingIgnoresObjectPropertyOrderButPreservesArrayOrder(bool reverseArray)
    {
        await using var h = new AcpHarness();
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "ordered_fixture", FriendlyName = "Ordered fixture", IsReadOnly = true,
            Description = "Returns the supplied value and ordered lines.",
            Handler = (Func<string, int[], string>)((value, lines) => value + ":" + string.Join(",", lines)),
            ConfirmationCheck = _ => "Allow the ordered fixture?"
        });
        var send = h.Service.SendAsync("match the original arguments");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Update(new
        {
            sessionUpdate = "tool_call", toolCallId = "ordered", status = "in_progress",
            rawInput = new { server = "oneware", tool = "ordered_fixture", arguments = new { lines = new[] { 1, 2 }, value = "value" } }
        });
        var call = h.Peer.CallTool("ordered", "ordered_fixture",
            new { value = "value", lines = reverseArray ? new[] { 2, 1 } : new[] { 1, 2 } }, announce: false);
        if (!reverseArray)
        {
            await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
            Assert.Empty(h.Starts);
            Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        }
        var result = await call;
        Assert.Equal(reverseArray, result.GetProperty("isError").GetBoolean());
        if (reverseArray)
        {
            Assert.Empty(h.Starts);
            Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
        }
        else
        {
            Assert.Single(h.Starts);
            Assert.Equal(JsonSerializer.Serialize("value:1,2"), result.GetProperty("content")[0].GetProperty("text").GetString());
        }
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task LaterConflictingNoticeDoesNotReplaceTheFirstCallOrItsPendingDecision()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("use the first notice");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Tool("first");
        var call = h.Peer.Call("first");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var permission = Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
        h.Peer.Tool("first", value: "conflicting");
        Assert.Same(permission, Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()));
        Assert.Empty(h.Starts);
        permission.AllowCommand.Execute(null);
        var result = await call;
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal(JsonSerializer.Serialize("ACP_FIXTURE_value"), result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Single(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task MissingNoticeTimesOutOnlyThatHttpRequestAndALaterMatchingRetryCanRun()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("retry after a missing notice");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var expired = await h.Peer.Call("late").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(expired.GetProperty("isError").GetBoolean());
        Assert.Equal("No matching ACP tool call was observed.", expired.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(h.Starts);
        Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
        h.Peer.Tool("late");
        var retry = h.Peer.Call("late");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        Assert.False((await retry).GetProperty("isError").GetBoolean());
        Assert.Single(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task ConcurrentCallbacksForOneCallAskOnceAndInvokeOnce()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("one logical call");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var calls = Enumerable.Range(0, 4).Select(_ => h.Peer.Call("shared")).ToArray();
        h.Peer.Tool("shared");
        await AcpHarness.Until(() => calls.Count(c => c.IsCompleted) == 3 &&
                                    h.Events.OfType<ChatPermissionRequestEvent>().Any());
        foreach (var duplicate in calls.Where(c => c.IsCompleted))
            Assert.True((await duplicate).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        var results = await Task.WhenAll(calls);
        Assert.Single(results, r => !r.GetProperty("isError").GetBoolean());
        Assert.Single(h.Starts);
        Assert.True((await h.Peer.Call("shared")).GetProperty("isError").GetBoolean());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
        Assert.Single(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateDuringInvocationOrAfterItsResultCannotRunAgain(bool fail)
    {
        await using var h = new AcpHarness();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "blocked_fixture", FriendlyName = "Blocked fixture", IsReadOnly = true,
            Description = "Waits for the fixture to release the invocation.",
            Handler = (Func<string, Task<string>>)(async value =>
            {
                Interlocked.Increment(ref invocations);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (fail) throw new InvalidOperationException("fixture failure");
                return value;
            })
        });
        var send = h.Service.SendAsync("keep duplicate calls out");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var call = h.Peer.CallTool("blocked", "blocked_fixture", new { value = "value" });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((await h.Peer.CallTool("blocked", "blocked_fixture", new { value = "value" }, announce: false))
                .GetProperty("isError").GetBoolean());
            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally
        {
            release.TrySetResult(true);
        }
        Assert.Equal(fail, (await call).GetProperty("isError").GetBoolean());
        Assert.True((await h.Peer.CallTool("blocked", "blocked_fixture", new { value = "value" }, announce: false))
            .GetProperty("isError").GetBoolean());
        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.Single(h.Starts);
        h.Peer.Complete();
        await send;
    }
}
