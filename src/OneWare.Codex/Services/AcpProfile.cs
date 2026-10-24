using System.Text.Json;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

/// <summary>Runtime and authentication scoped to one OneWare app-data profile.</summary>
internal sealed partial record AcpProfile(string Executable, string Model, string Effort, string Directory)
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
}

internal static class AcpRegistration
{
    public static void Initialize(IServiceProvider services)
    {
        var paths = services.Resolve<IPaths>();
        var profile = AcpProfile.Read(paths.AppDataDirectory);
        var runtime = profile == null ? new AcpRuntime(paths, services.Resolve<IPackageService>(),
            services.Resolve<IPackageWindowService>()) : null;
        profile ??= runtime!.Profile;
        var host = new OneWareHost(services.Resolve<IAiFunctionProvider>(), services.Resolve<IMainDockService>(),
            services.Resolve<IProjectExplorerService>());
        var agents = services.Resolve<IChatAgentService>();
        var chat = new AcpChatService(host, profile, () => agents.SelectedAgent,
            dock: services.Resolve<IMainDockService>(), settings: services.Resolve<ISettingsService>(), agents: agents,
            runtime: runtime);
        services.Resolve<IChatManagerService>().RegisterChatService(chat);
        services.Resolve<IApplicationStateService>().RegisterShutdownTask(async () =>
        {
            await chat.DisposeAsync();
            return true;
        });
    }
}