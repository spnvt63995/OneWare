using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Headless;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public static class AcpLiveVerification
{
    // Explicitly invoked by the ACP verification runner, never by the default test suite.
    public static async Task RunAsync(string appData, string evidence, bool live)
    {
        var profile = AcpProfile.Read(Path.GetFullPath(appData)) ??
                      throw new InvalidOperationException("Missing isolated ACP profile.");
        Assert.Equal("gpt-6-luna", profile.Model);
        Assert.Equal("low", profile.Effort);
        Directory.CreateDirectory(evidence);
        var trace = new ConcurrentQueue<object>();
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AcpAdapterTests).Assembly).Dispatch(async () =>
        {
            ObservedConnection Factory() => new(profile, trace);
            await using var h = new AcpHarness(confirm: true, profile: profile, factory: Factory);
            h.Service.EventReceived += (_, e) =>
            {
                if (e is ChatPermissionRequestEvent permission)
                {
                    trace.Enqueue(new { kind = "original_host_approval", message = permission.Message });
                    permission.AllowCommand.Execute(null);
                }

                if (e is ChatErrorEvent error) trace.Enqueue(new { kind = "service_error", error.Message });
            };
            try
            {
                Assert.True(await h.Service.InitializeAsync(),
                    string.Join("\n", h.Events.OfType<ChatErrorEvent>().Select(e => e.Message)));
                if (!live) return true;
                await h.Service
                    .SendAsync(
                        "Call mcp__oneware__acp_fixture once with value ACP_R1. Reply only with its returned marker. Use no other tools.")
                    .WaitAsync(TimeSpan.FromSeconds(150));
                Assert.Empty(h.Events.OfType<ChatErrorEvent>());
                Assert.Single(h.Starts);
                Assert.Contains(h.Events.OfType<ChatMessageEvent>(), m => m.Content.Contains("ACP_FIXTURE_ACP_R1"));
                Assert.Equal("Codex ACP · gpt-6-luna · low", h.Service.SelectionText);
                var id = h.Service.CurrentSessionId!;
                trace.Enqueue(new
                {
                    kind = "first_turn", sessionId = id, invocations = h.Starts.Count,
                    selection = h.Service.SelectionText
                });
                await h.Service.DisposeAsync();
                await using var resumed = new AcpHarness(confirm: true, profile: profile, factory: Factory);
                resumed.Service.EventReceived += (_, e) =>
                {
                    if (e is ChatPermissionRequestEvent permission)
                    {
                        trace.Enqueue(new { kind = "resumed_original_host_approval" });
                        permission.AllowCommand.Execute(null);
                    }
                };
                Assert.True(await resumed.Service.LoadSessionAsync(id));
                Assert.Empty(resumed.Events);
                Assert.Empty(resumed.Starts);
                await resumed.Service
                    .SendAsync(
                        "What exact marker did the previous tool return? Reply with that marker only. Do not call tools.")
                    .WaitAsync(TimeSpan.FromSeconds(150));
                Assert.Equal(id, resumed.Service.CurrentSessionId);
                Assert.Empty(resumed.Events.OfType<ChatErrorEvent>());
                Assert.Empty(resumed.Starts);
                Assert.Contains(resumed.Events.OfType<ChatMessageEvent>(),
                    m => m.Content.Contains("ACP_FIXTURE_ACP_R1"));
                trace.Enqueue(new
                {
                    kind = "same_id_resume", sessionId = id, replayEvents = 0,
                    repeatedInvocations = resumed.Starts.Count
                });
                Console.WriteLine(
                    "ACP_LIVE_PASS: original registry/approval/MCP result, Luna/low, and same-ID resume.");
                return true;
            }
            finally
            {
                File.WriteAllText(Path.Combine(evidence, live ? "live.json" : "probe.json"),
                    JsonSerializer.Serialize(trace.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            }
        }, CancellationToken.None);
    }

    internal sealed class ObservedConnection : IAgentConnection
    {
        private readonly AcpConnection _inner;
        private readonly ConcurrentQueue<object> _trace;

        public ObservedConnection(AcpProfile profile, ConcurrentQueue<object> trace)
        {
            _inner = new AcpConnection(profile);
            _trace = trace;
            _inner.Notification += (method, parameters) =>
            {
                // Synthetic smoke content only; never record MCP bearer headers or authentication messages.
                if (method == "session/update") _trace.Enqueue(new { kind = "notification", method, parameters });
                Notification?.Invoke(method, parameters);
            };
        }

        public bool IsConnected => _inner.IsConnected;
        public event Action<string, JsonElement>? Notification;

        public event Action<Exception>? Disconnected
        {
            add => _inner.Disconnected += value;
            remove => _inner.Disconnected -= value;
        }

        public Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest
        {
            get => _inner.ServerRequest;
            set => _inner.ServerRequest = value;
        }

        public Task StartAsync(string executable, string directory, CancellationToken token) =>
            _inner.StartAsync(executable, directory, token);

        public Task NotifyAsync(string method, object parameters, CancellationToken token = default) =>
            _inner.NotifyAsync(method, parameters, token);

        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token = default)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            _trace.Enqueue(new
            {
                kind = "request", method,
                sessionId = p.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                selection = method == "session/set_config_option" ? p : default(JsonElement?),
                prompt = method is "session/prompt" or "_session/steering"
                    ? p.GetProperty("prompt")
                    : default(JsonElement?)
            });
            var response = await _inner.RequestAsync(method, parameters, token);
            if (method != "session/prompt") _trace.Enqueue(new { kind = "response", method, response });
            return response;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}