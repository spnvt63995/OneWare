using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using OneWare.Chat.Services;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;
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
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("read the fixture");
        await h.Peer.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var call = "call-first";
        Task<JsonElement> result;
        if (httpFirst)
        {
            result = h.Peer.Call(call);
            await Task.Delay(30);
            Assert.Empty(h.Starts);
            h.Peer.Tool(call);
        }
        else
        {
            h.Peer.Tool(call);
            result = h.Peer.Call(call);
        }

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
        Assert.Empty(h.Events);
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

internal sealed class AcpHarness : IAsyncDisposable
{
    public AcpPeer Peer { get; } = new();
    public AcpChatService Service { get; }
    public AiFunctionProvider Provider { get; }
    public OneWareHost Host { get; }
    public List<ChatEvent> Events { get; } = [];
    public List<AiFunctionStartedEvent> Starts { get; } = [];
    public string Workspace { get; }
    public AcpProfile Profile { get; }
    private readonly bool _confirm;
    public ChatAgentDefinition? Agent { get; set; }
    public IExtendedDocument? Document { get; set; }
    public Dictionary<string, IExtendedDocument> Documents { get; } = [];

    public AcpHarness(bool confirm = false, AcpProfile? profile = null, Func<IAgentConnection>? factory = null,
        bool initializeTools = true, ISettingsService? settings = null, IChatAgentService? agents = null)
    {
        _confirm = confirm;
        Workspace = profile == null
            ? Path.Combine(Path.GetTempPath(), "oneware-acp-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(profile.Directory, "verification-workspace");
        Directory.CreateDirectory(Workspace);
        Profile = profile ?? new AcpProfile("/unused/acp", "gpt-6-luna", "low", Path.Combine(Workspace, "profile"));
        var dock = InterfaceStub.Create<IMainDockService>((name, _) => name switch
        {
            "get_OpenFiles" => Documents, "get_CurrentDocument" => Document, _ => null
        });
        var project = InterfaceStub.Create<IProjectRoot>((name, _) => name == "get_RootFolderPath" ? Workspace : null);
        var projects =
            InterfaceStub.Create<IProjectExplorerService>((name, _) => name == "get_ActiveProject" ? project : null);
        Provider = new AiFunctionProvider(projects, dock, InterfaceStub.Create<IErrorService>((_, _) => null),
            InterfaceStub.Create<ITerminalManagerService>((_, _) => null),
            InterfaceStub.Create<IWindowService>((_, _) => null),
            InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? Profile.Directory : null), NullLogger.Instance,
            new AiFileEditService(dock, AcpTestSupport.Settings()));
        if (initializeTools) Provider.GetTools();
        Register("ACP_FIXTURE_");
        Host = new OneWareHost(Provider, dock, projects);
        Service = new AcpChatService(Host, Profile, () => Agent, factory ?? (() => Peer), dock, settings, agents);
        Service.EventReceived += (_, e) => Events.Add(e);
        Provider.FunctionStarted += (_, e) => Starts.Add(e);
    }

    public void Register(string prefix) => Provider.RegisterFunction(new OneWareAiFunction
    {
        Name = "acp_fixture", FriendlyName = "ACP verification",
        Description = "Returns an isolated verification marker.",
        IsReadOnly = true, Handler = (Func<string, string>)(value => prefix + value),
        ConfirmationCheck = _ => _confirm ? "Allow the isolated ACP verification tool?" : null
    });

    public static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync() => await Service.DisposeAsync();
}

internal sealed class AcpPeer : IAgentConnection
{
    public bool IsConnected { get; private set; }
    public event Action<string, JsonElement>? Notification;
    public event Action<Exception>? Disconnected;
    public Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest { get; set; }
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public TaskCompletionSource<bool> PromptStarted { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<JsonElement> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonElement _mcp;
    public bool ReplayOnResume { get; set; }
    public bool SupportsResume { get; set; } = true;
    public string? LastResumeMethod { get; private set; }
    public bool BlockInitialization { get; set; }
    public TaskCompletionSource<bool> Initializing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailResume { get; set; }
    public string? MissingOption { get; set; }
    public List<JsonElement> Prompts { get; } = [];
    public List<AcpSelection> PromptSelections { get; } = [];
    public List<(string Method, JsonElement Parameters)> Requests { get; } = [];
    public List<string> Notifications { get; } = [];
    public bool SupportsSteering { get; set; } = true;
    public bool SupportsImages { get; set; } = true;
    public string SteerOutcome { get; set; } = "injected";
    public bool BlockSteer { get; set; }
    public bool FailPrompt { get; set; }
    public string[]? ModelChoices { get; set; }
    public Dictionary<string, string[]>? EffortsByModel { get; set; }
    public string? RejectConfigId { get; set; }
    public string? BlockConfigId { get; set; }
    public TaskCompletionSource<bool> ConfigStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ReleaseConfig { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> SteeringReceived { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ReleaseSteer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Dictionary<string, string> _options = new()
    {
        ["mode"] = "read-only", ["model"] = "gpt-6-luna", ["reasoning_effort"] = "low",
        ["collaboration_mode"] = "default"
    };

    public int NewSessions { get; private set; }
    public int Resumes { get; private set; }
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public Task StartAsync(string executable, string directory, CancellationToken token)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token = default)
    {
        var p = Json(parameters);
        Requests.Add((method, p));
        if (method == "initialize")
        {
            Initializing.TrySetResult(true);
            if (BlockInitialization) await Task.Delay(Timeout.Infinite, token);
            return Json(new
            {
                protocolVersion = 1, agentCapabilities = new
                {
                    mcpCapabilities = new { http = true },
                    sessionCapabilities = SupportsResume ? new { resume = new { } } : (object)new { },
                    loadSession = true,
                    promptCapabilities = new { image = SupportsImages, embeddedContext = true }
                },
                _meta = new { steering = new { supported = SupportsSteering } }
            });
        }

        if (method is "session/new" or "session/resume" or "session/load")
        {
            if (method == "session/new") NewSessions++;
            else
            {
                Resumes++;
                LastResumeMethod = method;
                if (FailResume) throw new IOException("resume unavailable");
                if (ReplayOnResume)
                {
                    Tool("replayed");
                    Update(new
                    {
                        sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "old history" }
                    });
                }
            }

            _mcp = p.GetProperty("mcpServers")[0].Clone();
            return Json(new { sessionId = Id, configOptions = Config() });
        }

        if (method == "session/set_config_option")
        {
            if (p.GetProperty("configId").GetString() == BlockConfigId)
            {
                ConfigStarted.TrySetResult(true);
                await ReleaseConfig.Task.WaitAsync(token);
            }
            if (p.GetProperty("configId").GetString() == RejectConfigId)
                throw new IOException("fixture option rejected");
            _options[p.GetProperty("configId").GetString()!] = p.GetProperty("value").GetString()!;
            if (p.GetProperty("configId").GetString() == "model" && EffortsByModel != null &&
                EffortsByModel.TryGetValue(_options["model"], out var efforts) &&
                efforts.Length > 0 && !efforts.Contains(_options["reasoning_effort"]))
                _options["reasoning_effort"] = efforts[0];
            return Json(new { configOptions = Config() });
        }

        if (method == "_session/steering")
        {
            SteeringReceived.TrySetResult(true);
            if (BlockSteer) await ReleaseSteer.Task.WaitAsync(token);
            return Json(new { outcome = SteerOutcome });
        }

        if (method == "session/prompt")
        {
            if (FailPrompt) throw new IOException("fixture prompt rejected");
            var response = _response;
            Prompts.Add(p);
            PromptSelections.Add(new AcpSelection(_options["model"], _options["reasoning_effort"]));
            PromptStarted.TrySetResult(true);
            try
            {
                return await response.Task.WaitAsync(token);
            }
            finally
            {
                if (ReferenceEquals(_response, response))
                    _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        throw new NotSupportedException(method);
    }

    private string[] EffortChoices => EffortsByModel?.GetValueOrDefault(_options["model"]) ?? ["low", "medium"];

    private object[] Config() => _options.Where(x => x.Key != MissingOption &&
        (x.Key != "reasoning_effort" || EffortChoices.Length > 0)).Select(x => (object)new
    {
        id = x.Key, type = "select", currentValue = x.Value,
        options = (x.Key == "collaboration_mode" ? new[] { "default", "plan" } :
                x.Key == "reasoning_effort" ? EffortChoices :
                x.Key == "model" && ModelChoices != null ? ModelChoices : new[] { x.Value })
            .Select(value => new { value, name = value }).ToArray()
    }).ToArray();

    public Task NotifyAsync(string method, object parameters, CancellationToken token = default)
    {
        Notifications.Add(method);
        return Task.CompletedTask;
    }

    public void Tool(string id, string name = "acp_fixture", string value = "value") => Update(new
    {
        sessionUpdate = "tool_call", toolCallId = id, status = "in_progress",
        rawInput = new { server = "oneware", tool = name, arguments = new { value } }
    });

    public void Update(object update, string? session = null) =>
        Notification?.Invoke("session/update", Json(new { sessionId = session ?? Id, update }));

    public Task<JsonElement> Call(string id, string? session = null, bool omitCallId = false) =>
        CallTool(id, "acp_fixture", new { value = "value" }, false, session, omitCallId);

    public async Task<JsonElement> CallTool(string id, string name, object arguments, bool announce = true,
        string? session = null, bool omitCallId = false)
    {
        if (announce) Update(new
        {
            sessionUpdate = "tool_call", toolCallId = id, status = "in_progress",
            rawInput = new { server = "oneware", tool = name, arguments }
        });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization",
            _mcp.GetProperty("headers")[0].GetProperty("value").GetString());
        var meta = new Dictionary<string, string> { ["threadId"] = session ?? Id };
        if (!omitCallId) meta["callId"] = id;
        using var response = await client.PostAsJsonAsync(_mcp.GetProperty("url").GetString(), new
        {
            jsonrpc = "2.0", id = Guid.NewGuid().ToString(), method = "tools/call",
            @params = new { name, arguments, _meta = meta }
        });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").Clone();
    }

    public void Complete(object? usage = null)
    {
        Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "answer" } });
        _response.TrySetResult(Json(new { stopReason = "end_turn", usage }));
    }

    public void ResetPrompt()
    {
        PromptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Drop()
    {
        IsConnected = false;
        Disconnected?.Invoke(new IOException("connection lost"));
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}