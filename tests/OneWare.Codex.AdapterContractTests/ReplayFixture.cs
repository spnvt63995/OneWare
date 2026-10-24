namespace OneWare.Codex.AdapterContractTests;

internal sealed record Message(string Id, string Role, string Text);

/// <summary>
/// A synthetic load fence and an in-memory stand-in for OneWare-owned history. The transport is
/// responsible for tagging load-origin events; proving that ACP mapping is deliberately deferred.
/// Unknown/incomplete replay stays quarantined. No content-string deduplication or automatic merge.
/// </summary>
internal sealed class ReplayFixture(Owner owner, IEnumerable<Message> saved, ContractFixture calls,
    Fault fault = Fault.None)
{
    private readonly List<Message> _history = saved.ToList();
    private readonly List<Message> _replayedMessages = [];
    private long _generation;
    private long? _loading;
    private bool _invalidReplay;
    public Message[] History => _history.ToArray();
    public int Writes { get; private set; }
    public int Quarantined { get; private set; }
    public int Rejected { get; private set; }

    public long BeginLoad()
    {
        _loading = ++_generation;
        _replayedMessages.Clear();
        _invalidReplay = false;
        return _loading.Value;
    }

    private bool Fence(Owner source, long? load)
    {
        if (source != owner) { Rejected++; return true; }
        if (fault == Fault.ReplayAsLive) return false;
        // An explicit load origin remains replay even AFTER completion. Untagged traffic received
        // during load is also fenced; an adapter cannot release late replay merely by changing UI state.
        if (load != null || _loading != null) { Quarantined++; return true; }
        return false;
    }

    public void ObserveMessage(Owner source, long? load, Message message)
    {
        if (Fence(source, load))
        {
            if (source == owner && _loading != null && load == _loading)
            {
                _replayedMessages.Add(message);
                var known = _history.FirstOrDefault(x => x.Id == message.Id);
                if (string.IsNullOrWhiteSpace(message.Id) || known == null || known != message)
                    _invalidReplay = true;
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(message.Id)) { Rejected++; return; }
        var existing = _history.FirstOrDefault(x => x.Id == message.Id);
        if (existing != null)
        {
            if (existing != message) Rejected++;
            return;
        }
        _history.Add(message);
        Writes++;
    }

    public void ObserveTool(Owner source, long? load, ToolCall call)
    {
        if (!Fence(source, load)) calls.Notice(call);
    }

    public Receipt ObserveCallback(Owner source, long? load, string rpcId, ToolCall call) =>
        Fence(source, load) ? new Receipt { State = CallState.Rejected, Error = "history-replay" } : calls.Request(rpcId, call);

    public void ObservePermission(Owner source, long? load, ToolCall call)
    {
        if (Fence(source, load)) return;
        // Deliberate faulty route for the negative control: a replayed permission becomes a live call.
        calls.Notice(call);
        calls.Request("permission-" + call.CallId, call);
    }

    public bool CompleteLoad(long load, Owner returnedOwner, bool success = true)
    {
        if (_loading != load) return false;
        _loading = null;
        return success && returnedOwner == owner && !_invalidReplay;
    }
}
