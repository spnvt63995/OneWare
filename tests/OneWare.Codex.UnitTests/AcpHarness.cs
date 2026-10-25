using Microsoft.Extensions.Logging.Abstractions;
using OneWare.Chat.Services;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;

namespace OneWare.Codex.UnitTests;

internal sealed class AcpHarness : IAsyncDisposable
{
    public AcpPeer Peer { get; } = new();
    public AcpChatService Service { get; }
    public AiFunctionProvider Provider { get; }
    public OneWareHost Host { get; }
    public List<ChatEvent> Events { get; } = [];
    public List<AiFunctionStartedEvent> Starts { get; } = [];
    public string Workspace { get; }
    public string CurrentWorkspace { get; set; }
    public AcpProfile Profile { get; }
    private readonly bool _confirm;
    public ChatAgentDefinition? Agent { get; set; }
    public IExtendedDocument? Document { get; set; }
    public Dictionary<string, IExtendedDocument> Documents { get; } = [];

    public AcpHarness(bool confirm = false, AcpProfile? profile = null, Func<IAgentConnection>? factory = null,
        bool initializeTools = true, ISettingsService? settings = null, IChatAgentService? agents = null,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, AcpContextLimits>>>? contextCatalog = null)
    {
        _confirm = confirm;
        Workspace = profile == null
            ? Path.Combine(Path.GetTempPath(), "oneware-acp-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(profile.Directory, "verification-workspace");
        Directory.CreateDirectory(Workspace);
        CurrentWorkspace = Workspace;
        Profile = profile ?? new AcpProfile("/unused/acp", "gpt-6-luna", "low", Path.Combine(Workspace, "profile"));
        var dock = InterfaceStub.Create<IMainDockService>((name, _) => name switch
        {
            "get_OpenFiles" => Documents, "get_CurrentDocument" => Document, _ => null
        });
        var project = InterfaceStub.Create<IProjectRoot>((name, _) => name == "get_RootFolderPath" ? CurrentWorkspace : null);
        var projects =
            InterfaceStub.Create<IProjectExplorerService>((name, _) => name == "get_ActiveProject" ? project : null);
        Provider = new AiFunctionProvider(projects, dock, InterfaceStub.Create<IErrorService>((_, _) => null),
            InterfaceStub.Create<ITerminalManagerService>((_, _) => null),
            InterfaceStub.Create<IWindowService>((_, _) => null),
            InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? Profile.Directory : null), NullLogger.Instance,
            new AiFileEditService(dock, AcpTestSupport.Settings()));
        if (initializeTools) Provider.GetTools();
        Register("ACP_FIXTURE_");
        Host = new OneWareHost(Provider, dock, projects);
        Service = new AcpChatService(Host, Profile, () => Agent, factory ?? (() => Peer), dock, settings, agents,
            contextCatalog: contextCatalog);
        Service.EventReceived += (_, e) => Events.Add(e);
        Provider.FunctionStarted += (_, e) => Starts.Add(e);
    }

    public void Register(string prefix) => Provider.RegisterFunction(new OneWareAiFunction
    {
        Name = "acp_fixture", FriendlyName = "ACP verification",
        Description = "Returns an isolated verification marker.",
        IsReadOnly = true, Handler = (Func<string, string>)(value => prefix + value),
        ConfirmationCheck = _ => _confirm ? "Allow the isolated ACP verification tool?" : null
    });

    public static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync() => await Service.DisposeAsync();
}
