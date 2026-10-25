using System.Net.Http.Json;
using System.Text.Json;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;

namespace OneWare.Codex.UnitTests;

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
    public object[]? SessionUpdates { get; set; }
    public Func<Task>? OnSessionRequest { get; set; }
    public object[]? AdvertisedCommands { get; set; }
    public bool SupportsResume { get; set; } = true;
    public string? LastResumeMethod { get; private set; }
    public bool BlockInitialization { get; set; }
    public TaskCompletionSource<bool> Initializing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailResume { get; set; }
    public Exception? ResumeError { get; set; }
    public Exception? NewSessionError { get; set; }
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
            if (method == "session/new")
            {
                NewSessions++;
                if (NewSessionError != null) throw NewSessionError;
            }
            else
            {
                Resumes++;
                LastResumeMethod = method;
                if (ResumeError != null) throw ResumeError;
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
            if (SessionUpdates != null)
                foreach (var update in SessionUpdates) Update(update);
            if (AdvertisedCommands != null)
                Update(new { sessionUpdate = "available_commands_update", availableCommands = AdvertisedCommands });
            if (OnSessionRequest != null) await OnSessionRequest();
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

    public void CompleteSilently() => _response.TrySetResult(Json(new { stopReason = "end_turn" }));

    public void FailResponse(Exception error) => _response.TrySetException(error);

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
