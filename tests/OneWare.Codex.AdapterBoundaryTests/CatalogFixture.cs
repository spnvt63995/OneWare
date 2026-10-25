using System.Text.Json;

namespace OneWare.Codex.AdapterBoundaryTests;

internal sealed class CatalogFixture(bool ignoreMetadataFault = false)
{
    private readonly Dictionary<string, FunctionSpec> _local = new(StringComparer.Ordinal);
    private Dictionary<string, FunctionSpec> _published = new(StringComparer.Ordinal);
    private int _revision;
    public string SessionId { get; } = "session-a";
    public bool Busy { get; set; }
    public bool Ready { get; private set; }
    public int Reconnects { get; private set; }
    public int SyncAttempts { get; private set; }
    public string? Error { get; private set; }
    public List<string> ResumedIds { get; } = [];
    public List<string> InvokedHandlers { get; } = [];
    public List<BoundaryInvocation> Invocations { get; } = [];
    public IReadOnlyDictionary<string, FunctionSpec> Published => _published;
    public Func<IReadOnlyDictionary<string, FunctionSpec>, IReadOnlyDictionary<string, FunctionSpec>> ObserveBackend { get; set; } =
        offered => offered.ToDictionary();

    public void Register(FunctionSpec function)
    {
        _local[function.Name] = function;
        _revision++;
        Ready = false;
    }
    public void Remove(string name) { _local.Remove(name); _revision++; Ready = false; }

    public bool Synchronize()
    {
        if (Busy) return false;
        Error = null;
        for (var retry = 0; retry < 3; retry++)
        {
            SyncAttempts++;
            var revision = _revision;
            var offered = _local.ToDictionary();
            var descriptorChanged = !Same(_published, offered);
            if (descriptorChanged)
            {
                var observed = ObserveBackend(offered);
                if (revision != _revision || !Same(offered, observed)) continue;
                Reconnects++;
                ResumedIds.Add(SessionId);
            }
            if (revision != _revision) continue;
            _published = offered;
            Ready = true;
            return true;
        }
        Error = "catalog-not-stable-or-not-observed";
        Ready = false;
        return false;
    }
    private bool Same(IReadOnlyDictionary<string, FunctionSpec> a, IReadOnlyDictionary<string, FunctionSpec> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) &&
            (ignoreMetadataFault
                ? pair.Value.Name == other.Name && JsonElement.DeepEquals(BoundaryData.Json(pair.Value.Schema), BoundaryData.Json(other.Schema))
                : pair.Value.SameDescriptor(other)));

    public CapturedCall? Prepare(string nativeName, JsonElement arguments, bool policyAllows = true)
    {
        if (!Ready || !policyAllows || !_local.TryGetValue(nativeName, out var function) || !Valid(function, arguments)) return null;
        return new(function, BoundaryData.Call(tool: nativeName) with { Arguments = arguments.Clone() });
    }
    public bool Invoke(CapturedCall? call)
    {
        if (call == null || call.Consumed) return false;
        call.Consumed = true;
        Invocations.Add(call.Invocation);
        InvokedHandlers.Add(call.Function.Description);
        call.Function.Handler(call.Invocation);
        return true;
    }
    // Matches the documented required/additional-name preflight only, NOT full JSON Schema validation.
    private static bool Valid(FunctionSpec function, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        using var schema = JsonDocument.Parse(function.Schema);
        var s = schema.RootElement;
        if (s.TryGetProperty("required", out var required) && required.EnumerateArray().Any(x => !arguments.TryGetProperty(x.GetString()!, out _))) return false;
        if (s.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
        {
            if (!s.TryGetProperty("properties", out var properties)) return !arguments.EnumerateObject().Any();
            if (arguments.EnumerateObject().Any(p => !properties.TryGetProperty(p.Name, out _))) return false;
        }
        return true;
    }
}
internal sealed class CapturedCall(FunctionSpec function, BoundaryInvocation invocation)
{
    public FunctionSpec Function { get; } = function;
    public BoundaryInvocation Invocation { get; } = invocation;
    public bool Consumed { get; set; }
}
