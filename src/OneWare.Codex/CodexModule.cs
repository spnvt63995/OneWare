using OneWare.Codex.Services;
using OneWare.Essentials.Services;

namespace OneWare.Codex;

/// <summary>Registers the ACP provider and its managed runtime.</summary>
public sealed class CodexModule : OneWareModuleBase
{
    public override IReadOnlyCollection<string> Dependencies => ["ChatModule"];

    public override void Initialize(IServiceProvider serviceProvider)
    {
        var paths = serviceProvider.Resolve<IPaths>();
        var profile = AcpProfile.Read(paths.AppDataDirectory);
        var runtime = profile == null ? new AcpRuntime(paths, serviceProvider.Resolve<IPackageService>(),
            serviceProvider.Resolve<IPackageWindowService>()) : null;
        profile ??= runtime!.Profile;
        var host = new OneWareHost(serviceProvider.Resolve<IAiFunctionProvider>(), serviceProvider.Resolve<IMainDockService>(),
            serviceProvider.Resolve<IProjectExplorerService>());
        var agents = serviceProvider.Resolve<IChatAgentService>();
        var chat = new AcpChatService(host, profile, () => agents.SelectedAgent,
            dock: serviceProvider.Resolve<IMainDockService>(), settings: serviceProvider.Resolve<ISettingsService>(), agents: agents,
            runtime: runtime);
        serviceProvider.Resolve<IChatManagerService>().RegisterChatService(chat);
        serviceProvider.Resolve<IApplicationStateService>().RegisterShutdownTask(async () =>
        {
            await chat.DisposeAsync();
            return true;
        });
    }
}
