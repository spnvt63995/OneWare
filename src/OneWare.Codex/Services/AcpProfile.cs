using System.Text.Json;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

/// <summary>Runtime and authentication scoped to one OneWare app-data profile.</summary>
internal sealed record AcpProfile(string Executable, string Model, string Effort, string Directory)
{
    public string[] Arguments { get; init; } = [];
    public string? CodexExecutable { get; init; }
    public string Home => Path.Combine(Directory, "codex-home");
    public string Bindings => Path.Combine(Directory, "sessions");

    public static AcpProfile? Read(string appData)
    {
        var file = Path.Combine(appData, "AI", "acp.json");
        if (!File.Exists(file)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var root = json.RootElement;
        var executable = root.GetProperty("executable").GetString()!;
        var model = root.GetProperty("model").GetString()!;
        var effort = root.GetProperty("effort").GetString()!;
        if (!Path.IsPathFullyQualified(executable) || string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(effort))
            throw new InvalidDataException("ACP needs an absolute executable path, model and reasoning effort.");
        return new AcpProfile(executable, model, effort, Path.Combine(Path.GetFullPath(appData), "AI", "ACP"))
        {
            Arguments = root.TryGetProperty("arguments", out var args)
                ? args.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : [],
            CodexExecutable = root.TryGetProperty("codexExecutable", out var codex) ? codex.GetString() : null
        };
    }

    private static readonly object BindingSync = new();

    public void SaveBinding(string id, string cwd, AcpSelection? selection = null, bool canRecreate = false)
    {
        lock (BindingSync)
            WriteBinding(new Binding("Codex ACP", id, Path.GetFullPath(cwd), selection, CanRecreate: canRecreate));
    }

    private void WriteBinding(Binding binding)
    {
        System.IO.Directory.CreateDirectory(Bindings);
        var file = BindingPath(binding.SessionId);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(binding));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Binding ReadBinding(string id)
    {
        var binding = JsonSerializer.Deserialize<Binding>(File.ReadAllText(BindingPath(id)));
        if (binding is not { Provider: "Codex ACP" } || binding.SessionId != id ||
            !Path.IsPathFullyQualified(binding.Directory))
            throw new InvalidDataException("This session does not belong to this ACP profile.");
        return binding;
    }

    private Binding ResolveBinding(string id)
    {
        var binding = ReadBinding(id);
        if (binding.RuntimeSessionId is not { } runtime) return binding;
        var target = ReadBinding(runtime);
        if (target.RuntimeSessionId != null || target.Directory != binding.Directory || runtime == id)
            throw new InvalidDataException("Invalid ACP conversation binding.");
        return target;
    }

    public string LoadBinding(string id) { lock (BindingSync) return ResolveBinding(id).Directory; }
    public string LoadRuntimeSession(string id) { lock (BindingSync) return ResolveBinding(id).SessionId; }
    public AcpSelection? LoadSelection(string id) { lock (BindingSync) return ResolveBinding(id).Selection; }
    public bool CanRecreateSession(string id) { lock (BindingSync) return ResolveBinding(id).CanRecreate; }

    public void SaveSelection(string id, AcpSelection selection)
    {
        lock (BindingSync) WriteBinding(ResolveBinding(id) with { Selection = selection });
    }

    public void RequireSessionResume(string id)
    {
        lock (BindingSync)
        {
            var binding = ResolveBinding(id);
            if (binding.CanRecreate) WriteBinding(binding with { CanRecreate = false });
        }
    }

    public void RebindEmptySession(string conversation, string previous, string replacement)
    {
        lock (BindingSync)
        {
            var source = ReadBinding(conversation);
            var current = ResolveBinding(conversation);
            if (current.SessionId != previous || !current.CanRecreate ||
                File.Exists(BindingPath(replacement)))
                throw new InvalidDataException("The ACP conversation changed while reconnecting. Reopen it and retry.");
            WriteBinding(current with { SessionId = replacement });
            // Keep the OneWare history key stable, including across repeated command-only restarts.
            WriteBinding(source with { RuntimeSessionId = replacement, CanRecreate = false });
        }
    }

    private string BindingPath(string id)
    {
        // codex-acp session IDs are Codex UUIDs; never treat arbitrary remote IDs as file paths.
        if (!Guid.TryParse(id, out var parsed) || parsed.ToString() != id)
            throw new InvalidDataException("Unsupported Codex ACP session ID.");
        return Path.Combine(Bindings, id + ".json");
    }

    private sealed record Binding(string Provider, string SessionId, string Directory, AcpSelection? Selection = null,
        string? RuntimeSessionId = null, bool CanRecreate = false);

    private string DelegationDirectory(string owner)
    {
        _ = BindingPath(owner);
        return Path.Combine(Directory, "delegations", owner);
    }

    internal static bool IsAgentId(string id) => id.StartsWith("acp-agent-", StringComparison.Ordinal) &&
        Guid.TryParseExact(id[10..], "N", out var parsed) && "acp-agent-" + parsed.ToString("N") == id;

    public void SaveDelegation(AcpSavedDelegation value)
    {
        if (!IsAgentId(value.Id) || LoadBinding(value.OwnerSession) != value.Directory ||
            LoadBinding(value.SessionId) != value.Directory)
            throw new InvalidDataException("The delegated conversation does not belong to this ACP workspace.");
        var directory = DelegationDirectory(value.OwnerSession);
        System.IO.Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, value.Id + ".json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public (AcpSavedDelegation[] Records, bool Incomplete) LoadDelegations(string owner, string cwd)
    {
        var directory = DelegationDirectory(owner);
        if (!System.IO.Directory.Exists(directory)) return ([], false);
        var records = new List<AcpSavedDelegation>();
        var incomplete = false;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var value = JsonSerializer.Deserialize<AcpSavedDelegation>(File.ReadAllText(file));
                if (value is not { Version: 1, Definition.Tools: not null, Catalog.Agents: not null,
                        Catalog.Skills: not null, Skills: not null, Ancestors.Length: > 0 and <= 4 } ||
                    value.OwnerSession != owner || value.Directory != cwd || value.SessionId == owner ||
                    value.Id == null || !IsAgentId(value.Id) || Path.GetFileNameWithoutExtension(file) != value.Id ||
                    value.Turn < 1 || value.Turn == int.MaxValue ||
                    string.IsNullOrWhiteSpace(value.Definition.Id) || string.IsNullOrWhiteSpace(value.Definition.DisplayName) ||
                    value.Ancestors.Any(string.IsNullOrWhiteSpace) || value.Ancestors[^1] != value.Definition.Id ||
                    value.Ancestors.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Ancestors.Length ||
                    value.State is not ("running" or "completed" or "failed" or "cancelled" or "interrupted") ||
                    LoadBinding(value.SessionId) != cwd)
                    throw new InvalidDataException("Invalid delegated conversation binding.");
                records.Add(value);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                incomplete = true;
            }
        }
        return (records.ToArray(), incomplete);
    }
}

internal sealed record AcpSavedDelegation(int Version, string OwnerSession, string Directory, string Id,
    string SessionId, ChatAgentDefinition Definition, string? Description, AcpPluginCatalog Catalog,
    AcpPluginSkill[] Skills, string[] Ancestors, string? ParentId, int Turn, string State,
    string? Answer, string? Error);
