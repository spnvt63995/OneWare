using System.Text.Json;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    // The handle outlives a single child process. Only the exact owned ACP session can
    // be resumed; a missing session must never silently become a new agent.
    private sealed class DelegatedConversation(string id, ChatAgentDefinition definition, string? description,
        AcpPluginCatalog catalog, AcpPluginSkill[] skills, string[] ancestors, DelegatedConversation? parent)
    {
        public string Id { get; } = id;
        public ChatAgentDefinition Definition { get; } = definition;
        public string? Description { get; } = description;
        public AcpPluginCatalog Catalog { get; } = catalog;
        public AcpPluginSkill[] Skills { get; } = skills;
        public string[] Ancestors { get; } = ancestors;
        public DelegatedConversation? Parent { get; } = parent;
        public string? SessionId { get; set; }
        public Delegation Run { get; set; } = null!;
    }

    private async Task<object> FollowupAgentAsync(Prompt turn, JsonElement arguments, CancellationToken token)
    {
        var owner = _delegation?.Owner ?? this;
        var id = Text(arguments, "id");
        var task = Text(arguments, "task");
        if (string.IsNullOrWhiteSpace(task)) return OneWareMcpResult.Failure("Provide the follow-up instructions.");
        DelegatedConversation conversation;
        lock (owner._backgroundSync)
        {
            if (id == null || !owner._delegatedAgents.TryGetValue(id, out conversation!) ||
                !CanReadBackground(conversation.Run))
                return OneWareMcpResult.Failure("That agent does not belong to this conversation or agent.");
            if (conversation.Run.Active || !conversation.Run.Completion.Task.IsCompleted)
                return OneWareMcpResult.Failure("That agent is still running. Wait for its result before sending a follow-up.");
            if (conversation.SessionId == null || conversation.Run.Cancellation.IsCancellationRequested)
                return OneWareMcpResult.Failure("That agent has no resumable conversation or was cancelled.");
        }
        var background = arguments.TryGetProperty("background", out var mode) && mode.ValueKind == JsonValueKind.True;
        return await StartDelegatedTurnAsync(turn, conversation, task, background, true, token);
    }

    private async Task<object> StartDelegatedTurnAsync(Prompt turn, DelegatedConversation conversation,
        string task, bool background, bool followup, CancellationToken token)
    {
        var input = turn.Input!;
        var owner = _delegation?.Owner ?? this;
        var original = conversation.Definition;
        // A remembered child cannot widen the current parent turn's tool budget or escape
        // Ask/Plan. Its own original model, instructions and maximum tool budget stay fixed.
        var definition = new ChatAgentDefinition
        {
            Id = original.Id, DisplayName = original.DisplayName,
            Model = original.Model, ReasoningEffort = original.ReasoningEffort,
            IsReadOnly = original.IsReadOnly || input.ReadOnly,
            Tools = turn.Tools.Values.Where(t => !AcpPluginCatalog.IsPluginTool(t.Function.Name) &&
                    turn.Allows(t) && original.Tools!.Contains(t.Function.Name, StringComparer.OrdinalIgnoreCase))
                .Select(t => t.Function.Name).ToArray(),
            Instructions = original.Instructions + (followup ? "\n\nCurrent parent instructions:\n" + input.AgentInstructions : "")
        };
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(background ? owner._lifetime.Token : token);
        AcpChatService child;
        try
        {
            lock (_family.Sync)
            {
                ObjectDisposedException.ThrowIf(_disposed || _family.Closing || _family.AccountChanging, this);
                child = new AcpChatService(_host, _profile, () => definition, _factory, _dock, null, null,
                    _family, false, _readContextCatalog);
            }
        }
        catch { cancellation.Dispose(); throw; }

        Delegation? context = null;
        lock (owner._backgroundSync)
        {
            // Reserve the next turn atomically. A concurrent caller cannot send a second
            // prompt to this session, and a retired handle cannot be reinserted.
            if (Current(turn) && !owner._disposed && !token.IsCancellationRequested &&
                (!followup || owner._delegatedAgents.TryGetValue(conversation.Id, out var owned) &&
                    ReferenceEquals(owned, conversation) && CanReadBackground(conversation.Run) &&
                    !conversation.Run.Active && conversation.Run.Completion.Task.IsCompleted &&
                    !conversation.Run.Cancellation.IsCancellationRequested))
            {
                context = new Delegation(owner, _delegation?.RootTurn ?? turn, conversation,
                    followup ? checked(conversation.Run.Number + 1) : 1, _delegation, background,
                    Interlocked.Read(ref owner._conversationEpoch), cancellation);
                conversation.Run = context;
                child._delegation = context;
                owner._delegatedAgents[conversation.Id] = conversation;
            }
        }
        if (context == null)
        {
            try { await child.DisposeAsync(); }
            finally { cancellation.Dispose(); }
            return OneWareMcpResult.Failure("The agent is busy or the originating conversation changed.");
        }

        owner.SaveDelegation(context);
        Emit(new ChatSubAgentStartedEvent(context.Id, original.DisplayName +
            (followup ? " · Follow-up " + context.Number : ""))
        {
            Description = conversation.Description, Instructions = task, Model = definition.Model,
            ParentSubAgentId = _delegation?.Id, AgentId = _delegation?.Id, IsBackground = background
        });
        var execution = RunDelegationAsync(context, child, task);
        return background
            ? OneWareMcpResult.Success(new { id = conversation.Id, turn = context.Number, status = "running" })
            : await execution;
    }
}
