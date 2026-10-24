using System.Text.Json;

namespace OneWare.Codex.AdapterContractTests;

// Normalized synthetic envelopes, NOT an implementation of ACP's wire schema.
internal sealed record Owner(string Provider, string Profile, int Connection, string Session, int Prompt);
internal sealed record ToolCall(Owner Owner, string CallId, string Tool, string Arguments,
    string Server = "oneware");
internal sealed record Invocation(string SessionId, string ToolCallId, string Tool, string Arguments);
internal enum CallState { Waiting, Approval, Executing, Succeeded, Failed, Rejected, Expired }
internal enum Answer { Once, Chat, Deny }
internal enum Fault { None, RunBeforeMatch, IgnoreOwner, ForgetCompletedCall, AcceptStaleDecision, ReplayAsLive }

internal sealed class Receipt
{
    public CallState State { get; internal set; } = CallState.Waiting;
    public string? Error { get; internal set; }
}

/// <summary>
/// Bounded, in-memory proposed contract. No product assembly, process, HTTP endpoint, file tool,
/// profile, credential or model is opened. Logical time makes missing-event tests deterministic.
/// A logical call is retained for this fixture lifetime; no durable/crash-recovery guarantee.
/// </summary>
internal sealed class ContractFixture(bool confirmationRequired = false, Fault fault = Fault.None)
{
    private sealed class Slot(long deadline)
    {
        public long Deadline { get; } = deadline;
        public ToolCall? Notice;
        public ToolCall? Request;
        public Receipt? Receipt;
        public CallState State = CallState.Waiting;
        public string? Decision;
    }

    private readonly object _sync = new();
    private readonly Dictionary<(Owner, string), Slot> _calls = [];
    private readonly HashSet<(string, string, int, string)> _rpcIds = [];
    private readonly Dictionary<string, Slot> _decisions = [];
    private readonly HashSet<string> _grants = [];
    private readonly List<Invocation> _invocations = [];
    private Owner? _active;
    private long _clock;
    private long _decisionSequence;
    private int _approvalRequests;

    public Invocation[] Invocations { get { lock (_sync) return _invocations.ToArray(); } }
    public int ApprovalRequests { get { lock (_sync) return _approvalRequests; } }
    public int GrantCount { get { lock (_sync) return _grants.Count; } }
    public string[] PendingDecisions
    {
        get { lock (_sync) return _decisions.Where(x => x.Value.State == CallState.Approval).Select(x => x.Key).ToArray(); }
    }

    public void Begin(Owner owner)
    {
        lock (_sync)
        {
            End();
            _active = owner;
        }
    }

    public void End()
    {
        lock (_sync)
        {
            _active = null;
            _grants.Clear();
            foreach (var slot in _calls.Values.Where(x => x.State is CallState.Waiting or CallState.Approval or CallState.Executing))
                Reject(slot, "scope-ended");
        }
    }

    private bool Current(Owner owner) => fault == Fault.IgnoreOwner || _active == owner;
    private Slot Get(ToolCall call)
    {
        var key = (call.Owner, call.CallId);
        if (!_calls.TryGetValue(key, out var slot)) _calls[key] = slot = new Slot(_clock + 5);
        return slot;
    }

    public bool Notice(ToolCall call)
    {
        lock (_sync)
        {
            if (!Current(call.Owner) || !Valid(call)) return false;
            var slot = Get(call);
            if (slot.State is not (CallState.Waiting or CallState.Approval)) return false;
            if (slot.Notice is { } prior && !Same(prior, call))
            {
                Reject(slot, "conflicting-notice");
                return false;
            }
            slot.Notice = call;
            Join(slot);
            return true;
        }
    }

    public Receipt Request(string rpcId, ToolCall call)
    {
        lock (_sync)
        {
            if (!Current(call.Owner) || !Valid(call) || string.IsNullOrWhiteSpace(rpcId))
                return Rejection("invalid-owner-or-envelope");
            if (!_rpcIds.Add((call.Owner.Provider, call.Owner.Profile, call.Owner.Connection, rpcId)))
                return Rejection("duplicate-rpc-id");
            var slot = Get(call);
            if (fault == Fault.ForgetCompletedCall && slot.State is CallState.Succeeded or CallState.Failed)
            {
                var replacement = new Slot(_clock + 5) { Notice = slot.Notice };
                _calls[(call.Owner, call.CallId)] = slot = replacement;
            }
            if (slot.Request != null || slot.State != CallState.Waiting)
                return Rejection("duplicate-or-retired-logical-call");
            slot.Request = call;
            slot.Receipt = new Receipt();
            Join(slot);
            return slot.Receipt;
        }
    }

    private void Join(Slot slot)
    {
        if (slot.State != CallState.Waiting || slot.Request is not { } request) return;
        if (!Current(request.Owner)) { Reject(slot, "owner-changed"); return; }
        if (slot.Notice == null && fault != Fault.RunBeforeMatch) return;
        if (slot.Notice is { } notice && !Same(notice, request)) { Reject(slot, "call-mismatch"); return; }
        if (confirmationRequired && !_grants.Contains(request.Tool))
        {
            SetState(slot, CallState.Approval);
            slot.Decision = $"decision-{++_decisionSequence}";
            _decisions.Add(slot.Decision, slot);
            _approvalRequests++;
        }
        else Execute(slot);
    }

    public bool Resolve(string decision, Answer answer)
    {
        lock (_sync)
        {
            if (!_decisions.TryGetValue(decision, out var slot) || slot.Request == null) return false;
            if ((slot.State != CallState.Approval || !Current(slot.Request.Owner)) && fault != Fault.AcceptStaleDecision)
                return false;
            if (answer == Answer.Deny) { Reject(slot, "denied"); return true; }
            if (answer == Answer.Chat) _grants.Add(slot.Request.Tool);
            Execute(slot);
            return true;
        }
    }

    public void CancelDecision(string decision)
    {
        lock (_sync)
            if (_decisions.TryGetValue(decision, out var slot) && slot.State == CallState.Approval)
                Reject(slot, "approval-cancelled");
    }

    // Claim and the recording delegate run in one critical section. Completion is a later event,
    // so tests can inject duplicate callbacks while an invocation is still in flight.
    private void Execute(Slot slot)
    {
        var call = slot.Request!;
        if (!Current(call.Owner) && fault != Fault.AcceptStaleDecision) { Reject(slot, "owner-changed"); return; }
        SetState(slot, CallState.Executing);
        _invocations.Add(new Invocation(call.Owner.Session, call.CallId, call.Tool, call.Arguments));
    }

    public bool Finish(ToolCall call, bool success = true)
    {
        lock (_sync)
        {
            if (!_calls.TryGetValue((call.Owner, call.CallId), out var slot) || slot.State != CallState.Executing)
                return false;
            SetState(slot, success ? CallState.Succeeded : CallState.Failed);
            return true;
        }
    }

    public void Advance(long seconds)
    {
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (_sync)
        {
            _clock += seconds;
            foreach (var slot in _calls.Values.Where(x => x.State == CallState.Waiting && x.Deadline <= _clock))
            {
                SetState(slot, CallState.Expired);
                if (slot.Receipt != null) slot.Receipt.Error = "match-timeout";
            }
        }
    }

    private static Receipt Rejection(string reason) => new() { State = CallState.Rejected, Error = reason };
    private static void Reject(Slot slot, string reason)
    {
        SetState(slot, CallState.Rejected);
        if (slot.Receipt != null) slot.Receipt.Error = reason;
    }
    private static void SetState(Slot slot, CallState state)
    {
        slot.State = state;
        if (slot.Receipt != null) slot.Receipt.State = state;
    }

    private static bool Valid(ToolCall call)
    {
        var o = call.Owner;
        if (string.IsNullOrWhiteSpace(o.Provider) || string.IsNullOrWhiteSpace(o.Profile) || o.Connection <= 0 ||
            string.IsNullOrWhiteSpace(o.Session) || o.Prompt <= 0 || string.IsNullOrWhiteSpace(call.CallId) ||
            call.Server != "oneware" || string.IsNullOrWhiteSpace(call.Tool)) return false;
        try
        {
            using var json = JsonDocument.Parse(call.Arguments);
            return json.RootElement.ValueKind == JsonValueKind.Object && UniqueKeys(json.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool UniqueKeys(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() ==
                                value.EnumerateObject().Count() && value.EnumerateObject().All(p => UniqueKeys(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueKeys),
        _ => true
    };

    private static bool Same(ToolCall a, ToolCall b)
    {
        if (a.Owner != b.Owner || a.CallId != b.CallId || a.Server != b.Server || a.Tool != b.Tool) return false;
        using var x = JsonDocument.Parse(a.Arguments);
        using var y = JsonDocument.Parse(b.Arguments);
        return JsonElement.DeepEquals(x.RootElement, y.RootElement);
    }
}
