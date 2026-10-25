namespace OneWare.Codex.AdapterBoundaryTests;

internal sealed record Intent(bool Explicit, string? Value)
{
    public static Intent Inherit => new(false, null);
}
internal sealed record SelectionIntent(Intent Model, Intent Reasoning);
internal sealed record SessionIdentity(string Provider, string Profile, string Id, string Directory);
internal sealed record ResumeTicket(SessionIdentity Identity, int Connection, int Generation);
internal sealed record ResumeReply(string Id, string Directory, bool Success, string Model, string Reasoning);
internal sealed record QueuedSubmission(SessionIdentity Identity, int Connection, int Prompt, bool Steer,
    string Text);

internal sealed class SessionFixture(bool replaceIntentFault = false, bool replaceHistoryFault = false)
{
    private int _generation;
    private readonly string[] _history = ["saved-user-id:question", "saved-assistant-id:answer"];
    private ResumeTicket? _pending;
    private readonly HashSet<int> _controlledConnections = [];
    public SessionIdentity Identity { get; private set; } = new("codex-test", "isolated", "session-a", "/fixture/work");
    public int Connection { get; private set; } = 1;
    public int Prompt { get; private set; } = 1;
    public bool Loaded { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<string> History => Array.AsReadOnly(_history);
    public SelectionIntent Saved { get; set; } = new(Intent.Inherit, Intent.Inherit);
    public (string Model, string Reasoning)? Observed { get; private set; }
    public List<ResumeTicket> Requests { get; } = [];
    public List<QueuedSubmission> Dispatched { get; } = [];
    public List<QueuedSubmission> ReviewRequired { get; } = [];
    public List<string> NewSessionRequests { get; } = [];
    public List<string> HistoryWrites { get; } = [];

    public ResumeTicket BeginResume()
    {
        Loaded = false;
        Error = null;
        _pending = new(Identity, Connection, ++_generation);
        Requests.Add(_pending);
        return _pending;
    }
    public bool FinishResume(ResumeTicket ticket, ResumeReply reply)
    {
        if (ticket != _pending || ticket.Identity != Identity || ticket.Connection != Connection) return false;
        _pending = null;
        if (!reply.Success || reply.Id != Identity.Id || reply.Directory != Identity.Directory)
        {
            Error = "resume-requires-retry";
            return false;
        }
        Loaded = true;
        Observed = (reply.Model, reply.Reasoning);
        if (replaceHistoryFault) _history[0] = "backend-replayed-copy";
        if (replaceIntentFault) Saved = Saved with { Model = new(true, reply.Model), Reasoning = new(true, reply.Reasoning) };
        return true;
    }
    public void Reconnect(bool controlledRefresh = false)
    {
        if (controlledRefresh) _controlledConnections.Add(Connection);
        else _controlledConnections.Clear();
        Connection++;
        Loaded = false;
        _pending = null;
    }
    public void Select(SessionIdentity identity)
    {
        Identity = identity;
        Reconnect();
    }
    public void NextPrompt() => Prompt++;
    // Input ownership is independent of selection intent; product tests cover live options and content.
    public QueuedSubmission Queue(string text, bool steer = false) =>
        new(Identity, Connection, Prompt, steer, text);
    public bool Dispatch(QueuedSubmission submission)
    {
        var sameConnection = submission.Connection == Connection || _controlledConnections.Contains(submission.Connection);
        if (!Loaded || submission.Identity != Identity || !sameConnection || (submission.Steer && submission.Prompt != Prompt))
        {
            ReviewRequired.Add(submission);
            return false;
        }
        Dispatched.Add(submission);
        return true;
    }
    public Dictionary<string, string> OptionsPayload()
    {
        var result = new Dictionary<string, string>();
        if (Saved.Model.Explicit) result["model"] = Saved.Model.Value!;
        if (Saved.Reasoning.Explicit) result["reasoning"] = Saved.Reasoning.Value!;
        return result;
    }
}
