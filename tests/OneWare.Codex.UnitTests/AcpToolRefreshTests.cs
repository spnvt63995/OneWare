using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpToolRefreshTests
{
    [AvaloniaFact]
    public async Task IdleRegistrationBurstRefreshesSameSessionWithoutAnotherMessageOrReplay()
    {
        await using var h = new AcpHarness();
        await FirstTurn(h);
        var messages = h.Events.Count;
        h.Peer.ReplayOnResume = true;
        for (var i = 0; i < 6; i++) Register(h, "acp_added_" + i);
        await AcpHarness.Until(() => h.Peer.Resumes == 1);
        var names = await ToolNames(h.Peer);
        Assert.All(Enumerable.Range(0, 6), i => Assert.Contains("acp_added_" + i, names));
        Assert.Equal(h.Peer.Id, h.Service.CurrentSessionId);
        Assert.Equal(1, h.Peer.NewSessions);
        Assert.Single(h.Peer.Prompts);
        Assert.Equal(messages, h.Events.Count);
        Assert.Empty(h.Starts);
    }

    [AvaloniaFact]
    public async Task BusyTurnKeepsExistingToolsAndSteeringThenQueueUsesRefreshedTools()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("first");
        await h.Peer.PromptStarted.Task;
        await h.Service.SendAsync("queued before registration", ChatSendMode.Queue);
        Register(h, "acp_added");
        Assert.False((await h.Peer.CallTool("unchanged", "acp_fixture", new { value = "old" }))
            .GetProperty("isError").GetBoolean());
        await h.Service.SendAsync("continue", ChatSendMode.Steer);
        Assert.Equal(0, h.Peer.Resumes);
        Assert.DoesNotContain("acp_added", await ToolNames(h.Peer));
        h.Peer.Complete();
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        Assert.Contains("acp_added", await ToolNames(h.Peer));
        Assert.False((await h.Peer.CallTool("new", "acp_added", new { value = "new" }))
            .GetProperty("isError").GetBoolean());
        h.Peer.Complete();
        await send;
        Assert.Equal(1, h.Peer.Resumes);
        Assert.Equal(h.Peer.Id, h.Service.CurrentSessionId);
        Assert.Equal(2, h.Starts.Count);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task PendingApprovalStillRejectsReplacedHandler()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task;
        var call = h.Peer.CallTool("pending", "acp_fixture", new { value = "old" });
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        h.Register("replacement");
        h.Events.OfType<ChatPermissionRequestEvent>().Single().AllowForSessionCommand!.Execute(null);
        Assert.True((await call).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task FailedIdleRefreshRetainsSessionAndNextSendRetries()
    {
        await using var h = new AcpHarness();
        await FirstTurn(h);
        h.Peer.FailResume = true;
        Register(h, "acp_added");
        await AcpHarness.Until(() => h.Events.OfType<ChatErrorEvent>().Any());
        Assert.Equal(h.Peer.Id, h.Service.CurrentSessionId);
        h.Peer.FailResume = false;
        h.Events.Clear();
        var send = h.Service.SendAsync("retry");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        Assert.Contains("acp_added", await ToolNames(h.Peer));
        h.Peer.Complete();
        await send;
        Assert.Equal(1, h.Peer.NewSessions);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task DisposalCancelsPendingRefreshAndUnsubscribes()
    {
        await using var h = new AcpHarness();
        await FirstTurn(h);
        Register(h, "acp_added");
        await h.Service.DisposeAsync();
        Register(h, "acp_after_disposal");
        await Task.Delay(300);
        Assert.Equal(0, h.Peer.Resumes);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    private static async Task FirstTurn(AcpHarness h)
    {
        var send = h.Service.SendAsync("fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Peer.Complete();
        await send;
    }

    internal static void Register(AcpHarness h, string name) => h.Provider.RegisterFunction(new OneWareAiFunction
    {
        Name = name, Description = "Returns the isolated live plugin marker.", IsReadOnly = true,
        Handler = (Func<string, string>)(value => "TOOLS_REFRESH_OK_" + value)
    });

    private static async Task<string[]> ToolNames(AcpPeer peer)
    {
        var server = peer.Requests.Last(x => x.Method is "session/new" or "session/resume" or "session/load")
            .Parameters.GetProperty("mcpServers")[0];
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", server.GetProperty("headers")[0].GetProperty("value").GetString());
        using var response = await client.PostAsJsonAsync(server.GetProperty("url").GetString(),
            new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()!).ToArray();
    }
}
