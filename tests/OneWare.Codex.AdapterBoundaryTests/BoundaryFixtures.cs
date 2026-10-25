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
