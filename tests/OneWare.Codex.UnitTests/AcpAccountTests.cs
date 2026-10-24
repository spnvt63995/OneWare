using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.PackageManager;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpAccountTests
{
    [AvaloniaFact]
    public async Task FailedAccountReadMustBeRetriedBeforeTheProviderIsReady()
    {
        var state = new Identity { ReadFailures = 1 };
        var peers = new List<AccountPeer>();
        await using var h = new AcpHarness(factory: () => AddPeer(peers, state));
        Assert.False(await h.Service.InitializeAsync());
        Assert.False(await h.Service.InitializeAsync());
        Assert.Equal(2, peers.SelectMany(x => x.Calls).Count(x => x == "authentication/status"));
        Assert.Equal("Sign in to Codex", h.Service.Blocker!.Title);
    }

    [AvaloniaFact]
    public async Task SignInUsesAdvertisedAcpMethodAndRefreshesParallelChatsWithoutPrompting()
    {
        var state = new Identity();
        var peers = new List<AccountPeer>();
        await using var h = new AcpHarness(factory: () => AddPeer(peers, state));
        Assert.False(await h.Service.InitializeAsync());
        Assert.Equal("Sign in to Codex", h.Service.Blocker!.Title);
        Assert.True(h.Service.CanSignIn);
        var sibling = (AcpChatService)h.Service.CreateSession();
        Assert.False(await sibling.InitializeAsync());
        var previous = peers.ToArray();
        await h.Service.SignInCommand.ExecuteAsync(null);
        Assert.True(h.Service.IsAuthenticated);
        Assert.Null(h.Service.Blocker);
        Assert.True(sibling.IsAuthenticated);
        Assert.Contains(peers.SelectMany(x => x.Calls), x => x == "authenticate:chat-gpt");
        Assert.All(previous, p => Assert.False(p.IsConnected));
        Assert.DoesNotContain(peers.SelectMany(x => x.Calls), x => x.StartsWith("session/"));
    }

    [AvaloniaFact]
    public async Task CancelClosesLoginTransportAndRejectsLateSuccess()
    {
        var state = new Identity { HoldLogin = true };
        var peers = new List<AccountPeer>();
        await using var h = new AcpHarness(factory: () => AddPeer(peers, state));
        await h.Service.InitializeAsync();
        var login = h.Service.SignInCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers.Any(x => x.Calls.Contains("authenticate:chat-gpt")));
        var pending = peers.Last();
        Assert.True(h.Service.IsSigningIn);
        Assert.False(h.Service.CanLoadOptions);
        h.Service.CancelSignInCommand.Execute(null);
        await login.WaitAsync(TimeSpan.FromSeconds(10));
        pending.Push("account");
        await Task.Yield();
        Assert.False(pending.IsConnected);
        Assert.False(h.Service.IsAuthenticated);
        Assert.True(h.Service.CanSignIn);
        Assert.Contains("cancelled", h.Service.AccountMessage);
        Assert.Equal("Sign in to Codex", h.Service.Blocker!.Title);
    }

    [AvaloniaFact]
    public async Task LogoutRetiresSiblingConnectionsAndReturnsBothToLogin()
    {
        var state = new Identity { Kind = "account" };
        var peers = new List<AccountPeer>();
        await using var h = new AcpHarness(factory: () => AddPeer(peers, state));
        await h.Service.InitializeAsync();
        var sibling = (AcpChatService)h.Service.CreateSession();
        await sibling.InitializeAsync();
        var previous = peers.ToArray();
        Assert.True(h.Service.CanSignOut);
        await h.Service.SignOutCommand.ExecuteAsync(null);
        Assert.Equal("none", state.Kind);
        Assert.False(h.Service.IsAuthenticated);
        Assert.False(sibling.IsAuthenticated);
        Assert.All(previous, p => Assert.False(p.IsConnected));
        Assert.Contains("logout", peers.SelectMany(x => x.Calls));
    }

    [AvaloniaFact]
    public async Task ExternalCredentialsAreDisplayedWithoutOfferingRemoval()
    {
        var state = new Identity { Kind = "api_key" };
        await using var h = new AcpHarness(factory: () => new AccountPeer(state));
        Assert.True(await h.Service.InitializeAsync());
        Assert.True(h.Service.IsExternalAccount);
        Assert.False(h.Service.CanSignOut);
        Assert.False(h.Service.CanSignIn);
        Assert.Null(h.Service.Blocker);
    }

    [AvaloniaFact]
    public async Task OlderAgentWithoutAccountCapabilityRemainsUsable()
    {
        await using var h = new AcpHarness();
        Assert.True(await h.Service.InitializeAsync());
        Assert.False(h.Service.CanSignIn);
        Assert.False(h.Service.CanSignOut);
        Assert.DoesNotContain(h.Peer.Requests, x => x.Method.StartsWith("authentication/"));
    }

    [Fact]
    public void ManagedPackagesAndHomeAreSeparateFromLegacyAndCustomConfiguration()
    {
        var folder = Path.Combine(Path.GetTempPath(), "acp-account-" + Guid.NewGuid().ToString("N"));
        var packages = new List<Package>();
        var paths = InterfaceStub.Create<IPaths>((name, _) => name switch
        {
            "get_AppDataDirectory" => Path.Combine(folder, "appdata"),
            "get_NativeToolsDirectory" => Path.Combine(folder, "tools"), _ => null
        });
        var service = InterfaceStub.Create<IPackageService>((name, args) =>
        {
            if (name == "RegisterPackage") packages.Add((Package)args![0]!);
            return null;
        });
        var runtime = new AcpRuntime(paths, service, InterfaceStub.Create<IPackageWindowService>((_, _) => null));
        Assert.Equal(3, packages.Count);
        Assert.DoesNotContain(packages, x => x.Id == "codexcli");
        Assert.EndsWith(Path.Combine("AI", "ACP", "codex-home"), runtime.Profile.Home);
        Assert.Contains("codexacp-cli", runtime.Profile.CodexExecutable);
        Assert.False(Directory.Exists(folder));
        Assert.False(runtime.IsInstalled);
        Assert.Equal("gpt-6-luna", runtime.Profile.Model);
        Assert.Equal("low", runtime.Profile.Effort);
    }

    private static AccountPeer AddPeer(List<AccountPeer> peers, Identity state)
    {
        var peer = new AccountPeer(state);
        peers.Add(peer);
        return peer;
    }

    private sealed class Identity
    {
        public string Kind = "none";
        public bool HoldLogin;
        public int ReadFailures;
    }

    private sealed class AccountPeer(Identity state) : IAgentConnection
    {
        public bool IsConnected { get; private set; }
        public List<string> Calls { get; } = [];
        public event Action<string, JsonElement>? Notification;
        public event Action<Exception>? Disconnected { add { } remove { } }
        public Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest { get; set; }
        public Task StartAsync(string executable, string directory, CancellationToken token)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token = default)
        {
            Calls.Add(method == "authenticate" ? method + ":" + AcpPeer.Json(parameters).GetProperty("methodId").GetString() : method);
            if (method == "initialize") return AcpPeer.Json(new
            {
                protocolVersion = 1,
                agentCapabilities = new
                {
                    mcpCapabilities = new { http = true }, auth = new { logout = new { } },
                    _meta = new { authStatus = new { } }
                },
                authMethods = new[] { new { id = "chat-gpt", name = "ChatGPT" } }
            });
            if (method == "authenticate")
            {
                if (state.HoldLogin) await Task.Delay(Timeout.Infinite, token);
                state.Kind = "account";
                Push(state.Kind);
            }
            if (method == "logout") { state.Kind = "none"; Push(state.Kind); }
            if (method == "authentication/status" && state.ReadFailures-- > 0)
                throw new IOException("Account read unavailable");
            if (method == "authentication/status") return AcpPeer.Json(new
            {
                type = state.Kind switch { "account" => "chat-gpt", "api_key" => "api-key", _ => "unauthenticated" },
                email = state.Kind == "account" ? "test@example.invalid" : ""
            });
            return AcpPeer.Json(new { });
        }

        public void Push(string kind) => Notification?.Invoke("_auth/status_update",
            AcpPeer.Json(new { authStatus = new { kind, label = "ChatGPT", account = new { email = "test@example.invalid", plan = "pro" } } }));
        public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
}
