using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Headless;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public static class AcpToolRefreshLiveVerification
{
    // Opt-in runtime verification: two short prompts against an isolated ACP profile.
    public static async Task RunAsync(string appData, string evidence)
    {
        var profile = AcpProfile.Read(Path.GetFullPath(appData)) ??
                      throw new InvalidOperationException("Missing isolated ACP profile.");
        Assert.Equal("gpt-6-luna", profile.Model);
        Assert.Equal("low", profile.Effort);
        Directory.CreateDirectory(evidence);
        var trace = new ConcurrentQueue<object>();
        var resumes = 0;
        var prompts = 0;
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AcpAdapterTests).Assembly).Dispatch(async () =>
        {
            await using var h = new AcpHarness(profile: profile, factory: () =>
                new ObservedConnection(profile, trace, () => Interlocked.Increment(ref resumes),
                    () => Interlocked.Increment(ref prompts)));
            h.Service.EventReceived += (_, e) =>
            {
                if (e is ChatErrorEvent error) trace.Enqueue(new { kind = "error", error.Message });
            };
            try
            {
                Assert.True(await h.Service.InitializeAsync(), h.Service.AccountStatusText + ": " + h.Service.AccountMessage);
                await h.Service.SendAsync("Call mcp__oneware__acp_fixture once with value INITIAL. " +
                    "Reply only with the returned marker. Use no other tools.").WaitAsync(TimeSpan.FromSeconds(150));
                Assert.Empty(h.Events.OfType<ChatErrorEvent>());
                Assert.Single(h.Starts);
                Assert.Contains(h.Events.OfType<ChatMessageEvent>(), x => x.Content.Contains("ACP_FIXTURE_INITIAL"));
                var id = h.Service.CurrentSessionId;
                AcpToolRefreshTests.Register(h, "acp_live_added");
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    while (Volatile.Read(ref resumes) == 0) await Task.Delay(25, timeout.Token);
                Assert.Equal(1, Volatile.Read(ref prompts));
                Assert.Equal(id, h.Service.CurrentSessionId);
                trace.Enqueue(new { kind = "idle_refresh_without_submission", sessionId = id, prompts, resumes });
                await h.Service.SendAsync("Call mcp__oneware__acp_live_added once with value LIVE. " +
                    "Reply with its returned marker, then the exact marker from our previous turn. " +
                    "Use no other tools.").WaitAsync(TimeSpan.FromSeconds(150));
                Assert.Empty(h.Events.OfType<ChatErrorEvent>());
                Assert.Equal(2, h.Starts.Count);
                Assert.Equal(id, h.Service.CurrentSessionId);
                var last = h.Events.OfType<ChatMessageEvent>().Last().Content;
                Assert.Contains("TOOLS_REFRESH_OK_LIVE", last);
                Assert.Contains("ACP_FIXTURE_INITIAL", last);
                Assert.Equal("Codex ACP · gpt-6-luna · low", h.Service.SelectionText);
                trace.Enqueue(new { kind = "passed", sessionId = id, prompts, resumes,
                    invocations = h.Starts.Count, h.Service.SelectionText, result = last });
                Console.WriteLine("ACP_TOOL_REFRESH_PASS: idle refresh, new plugin invocation, same session/history; 2 Luna/low prompts.");
                return true;
            }
            finally
            {
                File.WriteAllText(Path.Combine(evidence, "tools-live.json"),
                    JsonSerializer.Serialize(trace.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            }
        }, CancellationToken.None);
    }

    private sealed class ObservedConnection(AcpProfile profile, ConcurrentQueue<object> trace,
        Action resumed, Action prompted) : IAgentConnection
    {
        private readonly AcpConnection _inner = new(profile);
        public bool IsConnected => _inner.IsConnected;
        public event Action<string, JsonElement>? Notification
        { add => _inner.Notification += value; remove => _inner.Notification -= value; }
        public event Action<Exception>? Disconnected
        { add => _inner.Disconnected += value; remove => _inner.Disconnected -= value; }
        public Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest
        { get => _inner.ServerRequest; set => _inner.ServerRequest = value; }
        public Task StartAsync(string executable, string directory, CancellationToken token) =>
            _inner.StartAsync(executable, directory, token);
        public Task NotifyAsync(string method, object parameters, CancellationToken token = default) =>
            _inner.NotifyAsync(method, parameters, token);
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token = default)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            if (method == "session/prompt") prompted();
            if (method == "session/set_config_option") trace.Enqueue(new { kind = "selection", value = p });
            var result = await _inner.RequestAsync(method, parameters, token);
            if (method == "authentication/status") trace.Enqueue(new { kind = "auth_status",
                type = result.TryGetProperty("type", out var type) ? type.GetString() : null,
                isolatedAuthFile = File.Exists(Path.Combine(profile.Home, "auth.json")) });
            if (method is "session/resume" or "session/load") resumed();
            return result;
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
