using System.Text.Json;

namespace OneWare.Codex.AdapterBoundaryTests;

// Normalized, in-memory contract probes. These types are not ACP wire messages or product services.
internal sealed record BoundaryOwner(string Provider, string Profile, int Connection, string Session,
    int Prompt, string Directory);
internal sealed record BoundaryInvocation(BoundaryOwner Owner, string CallId, string Tool, JsonElement Arguments);
internal sealed record FunctionSpec(string Name, string Description, string Schema, bool ReadOnly,
    Func<BoundaryInvocation, object?> Handler, Func<JsonElement, string?>? Confirmation = null);
internal static class BoundaryData
{
    public static readonly BoundaryOwner Owner = new("codex-test", "isolated", 1, "session-a", 1, "/fixture/work");
    public const string Schema = """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""";
    public static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    public static FunctionSpec Tool(string name = "readFile", Func<BoundaryInvocation, object?>? handler = null) =>
        new(name, "original description", Schema, true, handler ?? (_ => "original result"));
    public static BoundaryInvocation Call(string id = "call-a", string tool = "readFile", string args = """{"path":"a.txt"}""") =>
        new(Owner, id, tool, Json(args));
}
internal enum PermissionAnswer { Once, Chat, Deny }
internal sealed class PermissionAttempt(FunctionSpec function, BoundaryInvocation call, string[]? keys)
{
    public FunctionSpec Function { get; } = function;
    public BoundaryInvocation Call { get; } = call;
    public string[]? Keys { get; } = keys;
    public string State { get; internal set; } = "new";
}
internal sealed class PermissionFixture(bool anyPrefixFault = false)
{
    private readonly HashSet<string> _grants = [];
    public int ApprovalRequests { get; private set; }
    public int GrantCount => _grants.Count;
    public List<BoundaryInvocation> Invocations { get; } = [];

    // Fixed-owner decision probes take explicit keys; product tests cover parsing and conversation lifetime.
    public PermissionAttempt Request(FunctionSpec function, BoundaryInvocation call, string[]? approvalKeys, bool policyAllows = true)
    {
        var keys = approvalKeys?.ToArray();
        var attempt = new PermissionAttempt(function, call with { Arguments = call.Arguments.Clone() }, keys);
        if (!policyAllows || call.Owner != BoundaryData.Owner || function.Name != call.Tool)
        {
            attempt.State = "denied";
            return attempt;
        }
        var reason = function.Confirmation?.Invoke(call.Arguments);
        var granted = keys is { Length: > 0 } &&
                      (anyPrefixFault ? keys.Any(_grants.Contains) : keys.All(_grants.Contains));
        if (reason == null || granted) Execute(attempt);
        else { attempt.State = "pending"; ApprovalRequests++; }
        return attempt;
    }
    public bool Resolve(PermissionAttempt attempt, PermissionAnswer answer)
    {
        if (attempt.State != "pending" || attempt.Call.Owner != BoundaryData.Owner) return false;
        if (answer == PermissionAnswer.Deny) { attempt.State = "denied"; return true; }
        if (answer == PermissionAnswer.Chat)
        {
            if (attempt.Keys is not { Length: > 0 }) return false;
            _grants.UnionWith(attempt.Keys);
        }
        Execute(attempt);
        return true;
    }
    private void Execute(PermissionAttempt attempt)
    {
        attempt.State = "executed";
        Invocations.Add(attempt.Call);
        attempt.Function.Handler(attempt.Call);
    }
}

internal sealed class PresentationRow(BoundaryInvocation call, bool clientTool)
{
    public BoundaryInvocation Call { get; } = call;
    public bool ClientTool { get; } = clientTool;
    public List<string> Progress { get; } = [];
    public int Completions { get; internal set; }
    public string? ProposedText { get; internal set; }
    public string Review { get; internal set; } = "none";
}
internal sealed class PresentationFixture(bool routeToSelectedFault = false)
{
    private readonly Dictionary<(BoundaryOwner, string), PresentationRow> _rows = [];
    private readonly HashSet<(BoundaryOwner, string)> _returned = [];
    public IReadOnlyCollection<PresentationRow> Rows => _rows.Values;
    public PresentationRow? Selected { get; private set; }
    public List<BoundaryInvocation> Invocations { get; } = [];
    public List<(BoundaryInvocation Call, object? Result)> ModelResults { get; } = [];
    public string DocumentText { get; private set; } = "original text";

    public PresentationRow Invoke(BoundaryInvocation call, bool clientTool = false)
    {
        var key = (call.Owner, call.CallId);
        if (_rows.TryGetValue(key, out var old)) return old;
        var row = new PresentationRow(call with { Arguments = call.Arguments.Clone() }, clientTool);
        _rows.Add(key, row);
        Selected = row;
        Invocations.Add(row.Call);
        return row;
    }
    // An own-MCP backend echo never becomes another invocation or another UI row.
    public void BackendEcho(BoundaryInvocation call, string updateKind) { _ = call; _ = updateKind; }
    public bool Progress(BoundaryInvocation call, string text)
    {
        var row = Find(call);
        if (row == null) return false;
        row.Progress.Add(text);
        return true;
    }
    public bool Complete(BoundaryInvocation call, bool originalClientEvent)
    {
        var row = Find(call);
        if (row == null || row.ClientTool != originalClientEvent || row.Completions != 0) return false;
        row.Completions++;
        return true;
    }
    public bool Return(BoundaryInvocation call, object? result)
    {
        if (Find(call) == null || !_returned.Add((call.Owner, call.CallId))) return false;
        ModelResults.Add((call, result));
        Complete(call, originalClientEvent: false);
        return true;
    }
    public bool ProposeEdit(BoundaryInvocation call, string proposed)
    {
        var row = Find(call);
        if (row == null) return false;
        row.ProposedText = proposed;
        row.Review = "pending";
        return true;
    }
    public bool ReviewEdit(BoundaryInvocation call, bool accept)
    {
        var row = Find(call);
        if (row?.Review != "pending") return false;
        if (accept) DocumentText = row.ProposedText!;
        row.Review = accept ? "accepted" : "rolled-back";
        return true;
    }
    private PresentationRow? Find(BoundaryInvocation call) =>
        routeToSelectedFault ? Selected : _rows.GetValueOrDefault((call.Owner, call.CallId));
}
