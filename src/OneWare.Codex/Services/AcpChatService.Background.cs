using System.Text.Json;
using OneWare.Codex.Protocol;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private readonly object _backgroundSync = new();
    private readonly Dictionary<string, DelegatedConversation> _delegatedAgents = new(StringComparer.Ordinal);
    private long _conversationEpoch;

    private static bool DescendsFrom(Delegation run, Delegation parent)
    {
        for (var ancestor = run.Conversation.Parent; ancestor != null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, parent.Conversation)) return true;
        return false;
    }

    private bool CanReadBackground(Delegation run) =>
        (_delegation == null || DescendsFrom(run, _delegation)) &&
        run.Epoch == Interlocked.Read(ref run.Owner._conversationEpoch) &&
        run.Owner._session == run.OwnerSession;

    private static object BackgroundSnapshot(Delegation run)
    {
        var finished = run.Completion.Task.IsCompleted;
        return new
        {
            id = run.Conversation.Id,
            turn = run.Number,
            status = !finished ? "running" : run.Cancellation.IsCancellationRequested ? "cancelled" :
                run.Error != null ? "failed" : "completed",
            result = finished ? run.Answer : null,
            error = finished ? run.Error : null
        };
    }

    private async Task<object> BackgroundResultAsync(JsonElement arguments, CancellationToken token)
    {
        var owner = _delegation?.Owner ?? this;
        var id = Text(arguments, "id");
        var action = Text(arguments, "action") ?? "status";
        if (action is not ("status" or "wait" or "cancel"))
            return OneWareMcpResult.Failure("Choose status, wait or cancel.");
        Delegation run;
        lock (owner._backgroundSync)
        {
            if (id == null || !owner._delegatedAgents.TryGetValue(id, out var conversation) ||
                !CanReadBackground(conversation.Run))
                return OneWareMcpResult.Failure("That background task does not belong to this conversation or agent.");
            run = conversation.Run;
            if (action == "cancel")
                foreach (var child in owner._delegatedAgents.Values.Select(x => x.Run).Where(x =>
                             ReferenceEquals(x, run) || DescendsFrom(x, run)))
                    if (child.Active) child.Cancellation.Cancel();
        }
        var observing = _delegation == null && action == "wait";
        if (observing) Interlocked.Increment(ref run.ResultWaiters);
        try
        {
            if (action is "wait" or "cancel") await run.Completion.Task.WaitAsync(token);
            lock (owner._backgroundSync)
            {
                if (!CanReadBackground(run)) return OneWareMcpResult.Failure("The originating conversation changed.");
                if (_delegation == null && run.Completion.Task.IsCompleted)
                    Interlocked.Exchange(ref run.ResultObserved, 1);
                return OneWareMcpResult.Success(BackgroundSnapshot(run));
            }
        }
        finally
        {
            if (observing)
            {
                Interlocked.Decrement(ref run.ResultWaiters);
                lock (owner._sync) owner.StartBackgroundDeliveryLocked();
            }
        }
    }

    private string DescribeBackgroundDelegations()
    {
        var owner = _delegation?.Owner ?? this;
        lock (owner._backgroundSync)
        {
            var runs = owner._delegatedAgents.Values.Select(x => x.Run).Where(CanReadBackground).Select(x => new
            {
                id = x.Conversation.Id, turn = x.Number, active = x.Active
            }).ToArray();
            return runs.Length == 0 ? "" : "\nDelegated agents in this conversation (reference data):\n" +
                JsonSerializer.Serialize(runs) +
                "\nContinue the user's request while tasks run. Inspect or cancel a task using " +
                "mcp__oneware__oneware_agent_result; background completion results are delivered automatically. " +
                "Use mcp__oneware__oneware_agent_followup with its id for same-task follow-ups to an idle agent.";
        }
    }

    private void CancelForegroundFunctions()
    {
        // Each ACP invocation already receives its prompt token. A blanket host cancellation
        // would also stop background tools attributed to the same original conversation.
        lock (_backgroundSync)
            if (_delegatedAgents.Values.Any(x => x.Run.Active && x.Run.Detached)) return;
        _host.Cancel(_session);
    }

    private async Task RetireBackgroundDelegationsAsync()
    {
        Delegation[] runs;
        lock (_backgroundSync)
        {
            _conversationEpoch++;
            runs = _delegatedAgents.Values.Select(x => x.Run).ToArray();
            _delegatedAgents.Clear();
            foreach (var run in runs)
                if (run.Active) run.Cancellation.Cancel();
        }
        ClearBackgroundDelivery();
        await Task.WhenAll(runs.Select(x => x.Completion.Task));
    }
}
