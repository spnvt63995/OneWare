using Xunit;

namespace OneWare.Codex.AdapterContractTests;

// Synthetic scope labels for the remaining approval/replay models, not ACP wire fields.
internal sealed record Owner(string Provider, string Profile, int Connection, string Session, int Prompt);
internal sealed record ToolCall(Owner Owner, string CallId, string Tool, string Arguments);
internal sealed record Invocation(string SessionId, string ToolCallId, string Tool, string Arguments);
internal enum CallState { Approval, Invoked, Rejected }
internal enum Answer { Once, Chat, Deny }
internal enum Fault { None, AcceptStaleDecision, ReplayAsLive }

internal sealed class Receipt
{
    public CallState State { get; internal set; }
    public string? Error { get; internal set; }
}

/// <summary>
/// Effect recorder for the independent approval and replay models. It does not model transport
/// matching, request IDs, deadlines, duplicate execution, JSON validation or stored chat grants.
/// </summary>
internal sealed class ContractEffects(bool confirmationRequired = false, Fault fault = Fault.None)
{
    private sealed record Decision(ToolCall Call, Receipt Receipt);
    private readonly Dictionary<string, Decision> _decisions = [];
    private readonly List<Invocation> _invocations = [];
    private Owner? _active;
    private long _decisionSequence;

    public List<ToolCall> ToolNotices { get; } = [];
    public Invocation[] Invocations => _invocations.ToArray();
    public int ApprovalRequests { get; private set; }
    public int ChatApprovalWrites { get; private set; }
    public string[] PendingDecisions =>
        _decisions.Where(x => x.Value.Receipt.State == CallState.Approval).Select(x => x.Key).ToArray();

    public void Begin(Owner owner)
    {
        End();
        _active = owner;
    }

    public void End()
    {
        _active = null;
        foreach (var decision in _decisions.Values.Where(x => x.Receipt.State == CallState.Approval))
            Reject(decision.Receipt, "scope-ended");
    }

    public void ObserveTool(ToolCall call) => ToolNotices.Add(call);

    public Receipt Request(ToolCall call)
    {
        var receipt = new Receipt();
        if (_active != call.Owner)
        {
            Reject(receipt, "owner-changed");
            return receipt;
        }
        if (confirmationRequired)
        {
            receipt.State = CallState.Approval;
            _decisions.Add($"decision-{++_decisionSequence}", new Decision(call, receipt));
            ApprovalRequests++;
        }
        else Invoke(call, receipt);
        return receipt;
    }

    public bool Resolve(string id, Answer answer)
    {
        if (!_decisions.TryGetValue(id, out var decision)) return false;
        if ((decision.Receipt.State != CallState.Approval || _active != decision.Call.Owner) &&
            fault != Fault.AcceptStaleDecision) return false;
        if (answer == Answer.Deny) Reject(decision.Receipt, "denied");
        else
        {
            if (answer == Answer.Chat) ChatApprovalWrites++;
            Invoke(decision.Call, decision.Receipt);
        }
        return true;
    }

    public void CancelDecision(string id)
    {
        if (_decisions.TryGetValue(id, out var decision) && decision.Receipt.State == CallState.Approval)
            Reject(decision.Receipt, "approval-cancelled");
    }

    private void Invoke(ToolCall call, Receipt receipt)
    {
        receipt.State = CallState.Invoked;
        _invocations.Add(new Invocation(call.Owner.Session, call.CallId, call.Tool, call.Arguments));
    }

    private static void Reject(Receipt receipt, string reason)
    {
        receipt.State = CallState.Rejected;
        receipt.Error = reason;
    }
}

internal static class ContractTestSupport
{
    internal static readonly Owner Scope = new("fixture-acp", "isolated-profile", 1, "session-a", 1);
    internal static ToolCall Call(string id = "call-a") =>
        new(Scope, id, "readFile", "{\"path\":\"fixture.txt\",\"lines\":[1,2]}");

    internal static ContractEffects Fixture(bool approval = false, Fault fault = Fault.None)
    {
        var effects = new ContractEffects(approval, fault);
        effects.Begin(Scope);
        return effects;
    }

    internal static void NoSideEffects(ContractEffects effects)
    {
        Assert.Empty(effects.ToolNotices);
        Assert.Empty(effects.Invocations);
        Assert.Equal(0, effects.ApprovalRequests);
        Assert.Equal(0, effects.ChatApprovalWrites);
    }
}
