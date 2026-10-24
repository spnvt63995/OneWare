using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.PackageManager;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpDefaultProviderTests
{
    [AvaloniaTheory]
    [InlineData(null, "Codex ACP")]
    [InlineData("Codex", "Codex ACP")]
    [InlineData("Other provider", "Other provider")]
    [InlineData("Codex ACP", "Codex ACP")]
    public async Task FirstUseAndRetiredProviderChooseAcpButExistingChoiceIsPreserved(string? saved, string expected)
    {
        using var disk = new AcpTestSupport.Profile();
        var statePath = Path.Combine(disk.Directory, "Chat", "ChatState.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        if (saved != null)
            File.WriteAllText(statePath, JsonSerializer.Serialize(new { Version = 2, LastServiceName = saved, LastSessionId = (string?)null }));
        var legacyHistory = Path.Combine(disk.Directory, "Chat", "History", "retired-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyHistory)!);
        File.WriteAllText(legacyHistory, "PRESERVED_OLD_CHAT");
        await using var h = new AcpHarness();
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = AcpTestSupport.Settings();
        settings.Register(ChatViewModel.MaxSessionHistoryKey, 50);
        var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? disk.Directory : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
            "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        var other = InterfaceStub.Create<IChatService>((name, _) => name switch
        {
            "get_Name" => "Other provider",
            "InitializeAsync" => Task.FromResult(false),
            "DisposeAsync" => ValueTask.CompletedTask, _ => null
        });
        chat.RegisterChatService(other);
        chat.RegisterChatService(h.Service);
        chat.InitializeContent();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(h.Service, chat.ChatServices[0]);
        Assert.Equal(expected, chat.SelectedChatService!.Name);
        Assert.Equal("PRESERVED_OLD_CHAT", File.ReadAllText(legacyHistory));
        chat.SaveState();
        Assert.Equal(expected, JsonDocument.Parse(File.ReadAllText(statePath)).RootElement.GetProperty("LastServiceName").GetString());
    }

    [AvaloniaFact]
    public async Task ModuleRegistersOnlyAcpItsThreeRuntimePackagesAndOwnedShutdown()
    {
        using var disk = new AcpTestSupport.Profile();
        await using var h = new AcpHarness();
        var registered = new List<IChatService>();
        var packages = new List<Package>();
        var shutdown = new List<Func<Task<bool>>>();
        var settings = AcpTestSupport.Settings();
        var services = new ServiceCollection();
        services.AddSingleton(InterfaceStub.Create<IPaths>((name, _) => name switch
        {
            "get_AppDataDirectory" => disk.Directory,
            "get_NativeToolsDirectory" => Path.Combine(disk.Directory, "runtime"), _ => null
        }));
        services.AddSingleton<IAiFunctionProvider>(h.Provider);
        services.AddSingleton(InterfaceStub.Create<IMainDockService>((name, _) => name == "get_OpenFiles"
            ? new Dictionary<string, OneWare.Essentials.ViewModels.IExtendedDocument>() : null));
        services.AddSingleton(InterfaceStub.Create<IProjectExplorerService>((_, _) => null));
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton(InterfaceStub.Create<IChatAgentService>((_, _) => null));
        services.AddSingleton(InterfaceStub.Create<IChatManagerService>((name, args) =>
        {
            if (name == "RegisterChatService") registered.Add((IChatService)args![0]!);
            return null;
        }));
        services.AddSingleton(InterfaceStub.Create<IApplicationStateService>((name, args) =>
        {
            if (name == "RegisterShutdownTask") shutdown.Add((Func<Task<bool>>)args![0]!);
            return null;
        }));
        services.AddSingleton(InterfaceStub.Create<IPackageService>((name, args) =>
        {
            if (name == "RegisterPackage") packages.Add((Package)args![0]!);
            return null;
        }));
        services.AddSingleton(InterfaceStub.Create<IPackageWindowService>((_, _) => null));
        var module = new CodexModule();
        module.RegisterServices(services);
        using var provider = services.BuildServiceProvider();
        module.Initialize(provider);
        var acp = Assert.IsType<AcpChatService>(Assert.Single(registered));
        Assert.IsAssignableFrom<IChatServiceWithDefaultPreference>(acp);
        Assert.Equal(new[] { "codexacp", "codexacp-cli", "codexacp-node" }, packages.Select(x => x.Id).Order());
        Assert.False(settings.HasSetting("Codex_Executable"));
        Assert.True(await Assert.Single(shutdown)());
    }
}
