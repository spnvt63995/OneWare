namespace OneWare.Codex.AdapterContractTests;

// Synthetic labels for decision ownership, not ACP wire fields.
internal sealed record ApprovalScope(int Connection, string Session, int Prompt);
internal sealed record ApprovalCall(ApprovalScope Owner, string CallId);
internal enum ApprovalState { Pending, Invoked, Rejected }
internal enum Answer { Once, Chat, Deny }
internal sealed class ApprovalReceipt
{
    public ApprovalState State { get; internal set; }
}

// Models pending decisions only. Product tests cover transport, invocation and stored chat grants.
internal sealed class ApprovalFixture(bool acceptStaleDecision = false)
{
    private sealed record Decision(ApprovalCall Call, ApprovalReceipt Receipt);
    private readonly Dictionary<string, Decision> _decisions = [];
    private readonly List<ApprovalCall> _invocations = [];
    private ApprovalScope? _active;
    private long _decisionSequence;

    public ApprovalCall[] Invocations => _invocations.ToArray();
    public int ChatApprovalWrites { get; private set; }
    public string[] PendingDecisions =>
        _decisions.Where(x => x.Value.Receipt.State == ApprovalState.Pending).Select(x => x.Key).ToArray();

    public void Begin(ApprovalScope owner)
    {
        End();
        _active = owner;
    }

    public void End()
    {
        _active = null;
        foreach (var decision in _decisions.Values.Where(x => x.Receipt.State == ApprovalState.Pending))
            decision.Receipt.State = ApprovalState.Rejected;
    }

    public ApprovalReceipt Request(ApprovalCall call)
    {
        var receipt = new ApprovalReceipt();
        if (_active != call.Owner)
            receipt.State = ApprovalState.Rejected;
        else
            _decisions.Add($"decision-{++_decisionSequence}", new Decision(call, receipt));
        return receipt;
    }

    public bool Resolve(string id, Answer answer)
    {
        if (!_decisions.TryGetValue(id, out var decision)) return false;
        if ((decision.Receipt.State != ApprovalState.Pending || _active != decision.Call.Owner) &&
            !acceptStaleDecision) return false;
        if (answer == Answer.Deny) decision.Receipt.State = ApprovalState.Rejected;
        else
        {
            if (answer == Answer.Chat) ChatApprovalWrites++;
            decision.Receipt.State = ApprovalState.Invoked;
            _invocations.Add(decision.Call);
        }
        return true;
    }

    public void CancelDecision(string id)
    {
        if (_decisions.TryGetValue(id, out var decision) && decision.Receipt.State == ApprovalState.Pending)
            decision.Receipt.State = ApprovalState.Rejected;
    }
}

internal static class ApprovalTestSupport
{
    internal static readonly ApprovalScope Scope = new(1, "session-a", 1);
    internal static ApprovalCall Call() => new(Scope, "call-a");

    internal static ApprovalFixture Fixture(bool acceptStaleDecision = false)
    {
        var fixture = new ApprovalFixture(acceptStaleDecision);
        fixture.Begin(Scope);
        return fixture;
    }
}
