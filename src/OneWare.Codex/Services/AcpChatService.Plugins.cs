using System.Diagnostics;
using System.Text.Json;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private sealed class Delegation(AcpChatService owner, Prompt rootTurn, DelegatedConversation conversation,
        int number, Delegation? parent, bool background, long epoch,
        CancellationTokenSource cancellation)
    {
        public AcpChatService Owner { get; } = owner;
        public Prompt RootTurn { get; } = rootTurn;
        public string OwnerSession => RootTurn.Session;
        public DelegatedConversation Conversation { get; } = conversation;
        public int Number { get; } = number;
        public string Id { get; } = number == 1 ? conversation.Id : conversation.Id + "-turn-" + number;
        public string[] Ancestors => Conversation.Ancestors;
        public AcpPluginCatalog Catalog => Conversation.Catalog;
        public Delegation? Parent { get; } = parent;
        public bool IsBackground { get; } = background;
        public bool Detached { get; } = background || parent?.Detached == true;
        public long Epoch { get; } = epoch;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Active = true;
        public bool Interrupted { get; init; }
        public string? Answer { get; set; }
        public string? Error { get; set; }
        public long ToolCalls { get; set; }
        public int DeliveryQueued;
        public int ResultObserved;
        public int ResultWaiters;
    }

    private Delegation? _delegation;

    private async Task<AcpPluginCatalog> CapturePluginsAsync(string request)
    {
        if (_delegation != null) return _delegation.Catalog;
        var registrations = await OneWareHost.Ui(() =>
            Task.FromResult((_host.GetAgents(), _host.GetSkillDirectories())));
        return await Task.Run(() => AcpPluginCatalog.Read(registrations.Item1, registrations.Item2, request),
            _lifetime.Token);
    }

    private string DisplayCallId(string id) => _delegation == null ? id : _delegation.Id + ":" + id;

    private async Task<object> InvokePluginAsync(Prompt turn, string tool, JsonElement arguments,
        CancellationToken token)
    {
        if (turn.Input is not { } input || !Current(turn) ||
            Path.GetFullPath((await _host.CaptureContextAsync()).Directory) != turn.Directory)
            return OneWareMcpResult.Failure("The originating ACP workspace or prompt changed.");
        token.ThrowIfCancellationRequested();
        if (tool == AcpPluginCatalog.AgentResultTool)
            return await BackgroundResultAsync(arguments, token);
        if (tool == AcpPluginCatalog.FollowupTool)
            return await FollowupAgentAsync(turn, arguments, token);
        if (tool == AcpPluginCatalog.LoadSkillTool)
        {
            var skill = input.Plugins.Skills.FirstOrDefault(s =>
                string.Equals(s.Name, Text(arguments, "name"), StringComparison.OrdinalIgnoreCase));
            if (skill == null) return OneWareMcpResult.Failure("That OneWare plugin skill is not registered.");
            Emit(new ChatSkillLoadedEvent(skill.Name, skill.Content));
            return OneWareMcpResult.Success(skill.Content);
        }

        var agent = input.Plugins.Agents.FirstOrDefault(a =>
            string.Equals(a.Name, Text(arguments, "agent"), StringComparison.OrdinalIgnoreCase));
        var task = Text(arguments, "task");
        if (agent == null || string.IsNullOrWhiteSpace(task))
            return OneWareMcpResult.Failure("Choose a registered OneWare agent and provide its task.");
        var ancestors = _delegation?.Ancestors ?? [];
        if (ancestors.Length >= 4 || ancestors.Contains(agent.Name, StringComparer.OrdinalIgnoreCase))
            return OneWareMcpResult.Failure("This delegation would repeat an ancestor or exceed four nested agents.");

        var preloaded = new List<AcpPluginSkill>();
        foreach (var name in agent.Skills ?? [])
        {
            var skill = input.Plugins.Skills.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (skill == null)
                return OneWareMcpResult.Failure($"Agent '{agent.Name}' requires the missing skill '{name}'.");
            if (!preloaded.Contains(skill)) preloaded.Add(skill);
        }

        // Intersect with the originating turn, including Ask/Plan. Delegation never widens tools.
        var allowed = turn.Tools.Values.Where(t => !AcpPluginCatalog.IsPluginTool(t.Function.Name) &&
                                                  turn.Allows(t) && (agent.Tools == null ||
                                                      agent.Tools.Contains(t.Function.Name,
                                                          StringComparer.OrdinalIgnoreCase)))
            .Select(t => t.Function.Name).ToArray();
        var model = string.IsNullOrWhiteSpace(agent.Model) ? input.Model : agent.Model;
        var effort = string.IsNullOrWhiteSpace(agent.ReasoningEffort) ? input.Effort : agent.ReasoningEffort;
        var definition = new ChatAgentDefinition
        {
            Id = agent.Name, DisplayName = agent.DisplayName ?? agent.Name,
            IsReadOnly = input.ReadOnly, Tools = allowed, Model = model, ReasoningEffort = effort,
            Instructions = input.AgentInstructions + "\n\n" + agent.Instructions +
                           string.Concat(preloaded.Select(s => "\n\nLoaded skill " + s.Name + ":\n" + s.Content))
        };
        var conversation = new DelegatedConversation("acp-agent-" + Guid.NewGuid().ToString("N"),
            definition, agent.Description, input.Plugins, preloaded.ToArray(), [..ancestors, agent.Name],
            _delegation?.Conversation);
        var background = arguments.TryGetProperty("background", out var mode) && mode.ValueKind == JsonValueKind.True;
        return await StartDelegatedTurnAsync(turn, conversation, task, background, false, token);
    }

    private async Task<object> RunDelegationAsync(Delegation context, AcpChatService child, string task)
    {
        // Publish the handle and started event before allowing a detached task to stream.
        await Task.Yield();
        var token = context.Cancellation.Token;
        using var cancellation = token.Register(() => child._lifetime.Cancel());
        var timer = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            if (context.Conversation.SessionId is { } session && !await child.LoadSessionAsync(session))
                throw new IOException("Could not resume the delegated conversation. Its session was not replaced.");
            token.ThrowIfCancellationRequested();
            foreach (var skill in context.Conversation.Skills) child.Emit(new ChatSkillLoadedEvent(skill.Name, skill.Content));
            await child.SendAsync(task);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            context.Error = error is OperationCanceledException ? "Delegated task cancelled." : error.Message;
        }
        finally
        {
            try { await child.DisposeAsync(); }
            catch (Exception error) { context.Error ??= error.Message; }
            context.Conversation.SessionId = child.CurrentSessionId ?? context.Conversation.SessionId;
            // Completion belongs to the original conversation even when Stop retired its prompt.
            if (!context.Owner._disposed && context.Owner._session == context.OwnerSession &&
                context.Epoch == Interlocked.Read(ref context.Owner._conversationEpoch))
                context.Owner.Emit(new ChatSubAgentCompletedEvent(context.Id, context.Error == null || token.IsCancellationRequested)
                {
                    Cancelled = token.IsCancellationRequested, Error = context.Error,
                    Duration = timer.Elapsed, TotalTokens = child._totalInput + child._totalOutput,
                    TotalToolCalls = context.ToolCalls
                });
            lock (context.Owner._backgroundSync)
            {
                context.Active = false;
                context.Owner.SaveDelegation(context);
                context.Completion.TrySetResult(true);
                context.Cancellation.Dispose();
            }
            context.Owner.QueueCompletedAgent(context);
        }
        return context.Error != null
            ? OneWareMcpResult.Failure(JsonSerializer.Serialize(BackgroundSnapshot(context)))
            : OneWareMcpResult.Success(BackgroundSnapshot(context));
    }

    private void ForwardDelegatedEvent(Delegation context, ChatEvent value)
    {
        if (value is ChatMessageEvent message) context.Answer = message.Content;
        if (value is ChatErrorEvent error) context.Error = error.Message;
        if (value is ChatToolExecutionStartEvent) context.ToolCalls++;
        if (!context.Active || context.Cancellation.IsCancellationRequested || context.Owner._disposed ||
            context.Owner._session != context.OwnerSession ||
            context.Epoch != Interlocked.Read(ref context.Owner._conversationEpoch) ||
            !context.Detached && !context.Owner.Current(context.RootTurn)) return;
        // Keep the original host responsible for rendering, progress and approval commands.
        // Child user echoes and Idle must never finish or append to the parent turn.
        ChatEvent? forwarded = value switch
        {
            ChatMessageDeltaEvent x => new ChatMessageDeltaEvent(x.Content, x.MessageId) { AgentId = context.Id },
            ChatMessageEvent x => new ChatMessageEvent(x.Content, x.MessageId) { AgentId = context.Id, Model = x.Model },
            ChatReasoningDeltaEvent x => new ChatReasoningDeltaEvent(x.Content, x.ReasoningId) { AgentId = context.Id },
            ChatReasoningEvent x => new ChatReasoningEvent(x.Content, x.ReasoningId) { AgentId = context.Id },
            ChatSkillLoadedEvent x => new ChatSkillLoadedEvent(x.SkillName, x.Content) { AgentId = context.Id },
            ChatToolExecutionStartEvent x => new ChatToolExecutionStartEvent(x.Tool,
                x.ToolCallId == null ? null : DisplayCallId(x.ToolCallId), x.IsClientTool)
                { AgentId = context.Id, Detail = x.Detail },
            ChatToolExecutionCompleteEvent x => new ChatToolExecutionCompleteEvent(DisplayCallId(x.ToolCallId),
                x.Success, x.Output) { AgentId = context.Id },
            ChatErrorEvent x => new ChatErrorEvent(x.Message ?? "Delegated task failed.") { AgentId = context.Id },
            ChatPermissionRequestEvent or ChatUserInputRequestEvent or
                ChatSubAgentStartedEvent or ChatSubAgentCompletedEvent => value,
            _ => null
        };
        if (forwarded != null) context.Owner.Emit(forwarded);
    }
}
