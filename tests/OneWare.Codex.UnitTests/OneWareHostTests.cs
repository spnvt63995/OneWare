using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OneWare.Chat.Services;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class OneWareHostTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContextSnapshotsEditorOnUiThreadAndKeepsWorkspace(bool hasProject, bool hasDocument)
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "host-project");
        var documentPath = Path.Combine(Path.GetTempPath(), "host-editor", "draft.txt");
        var dirty = true;
        var document = InterfaceStub.Create<IExtendedDocument>((name, _) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            return name switch
            {
                "get_FullPath" => documentPath,
                "get_IsDirty" => dirty,
                "get_IsReadOnly" => true,
                _ => null
            };
        });
        var open = new Dictionary<string, IExtendedDocument> { [documentPath] = document };
        var dock = InterfaceStub.Create<IMainDockService>((name, _) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            return name switch
            {
                "get_CurrentDocument" => hasDocument ? document : null,
                "get_OpenFiles" => open,
                _ => null
            };
        });
        var project = InterfaceStub.Create<IProjectRoot>((name, _) => name == "get_RootFolderPath" ? projectPath : null);
        var projects = InterfaceStub.Create<IProjectExplorerService>((name, _) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            return name == "get_ActiveProject" && hasProject ? project : null;
        });
        var host = new OneWareHost(InterfaceStub.Create<IAiFunctionProvider>((_, _) => null), dock, projects);
        // A background caller must receive detached values, never live editor objects.
        var context = await Task.Run(host.CaptureContextAsync);
        var expected = Path.GetFullPath(hasProject ? projectPath : hasDocument
            ? Path.GetDirectoryName(documentPath)! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        dirty = false;
        open.Clear();

        Assert.Equal(expected, context.Directory);
        Assert.Equal(hasDocument ? documentPath : null, context.ActiveDocument);
        Assert.Equal(new OneWareDocument(documentPath, true, true), Assert.Single(context.OpenDocuments));

    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("**Copilot wants to read a file**")]
    [InlineData("Extension: **Copilot wants to read a file**")]
    [InlineData("Custom confirmation")]
    public async Task HostPreservesRegisteredConfirmationHeading(string? original)
    {
        var (provider, host) = CreateHost();
        provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "host_check", Description = "Check the host boundary", IsReadOnly = false,
            Handler = (Func<string, string>)(value => value),
            ConfirmationCheck = args =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                Assert.Equal("unchanged", args["value"]);
                return original;
            }
        });
        var args = new AIFunctionArguments { ["value"] = "unchanged" };
        Assert.Equal(original, await Task.Run(() => host.ConfirmationAsync("host_check", args)));
    }

    [AvaloniaFact]
    public async Task OriginalRegistryChangesReachHostWithoutTransportNamesOrCachedPolicy()
    {
        var (provider, host) = CreateHost();
        host.GetTools(); // Finish lazy built-in registration before observing the extension.
        var changes = 0;
        EventHandler changed = (sender, _) => { Assert.Same(provider, sender); changes++; };
        host.ToolsChanged += changed;
        void Register(bool readOnly, string reply) => provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "host_extension", FriendlyName = "Host extension", Description = "Native host schema",
            IsReadOnly = readOnly, Handler = (Func<string, string>)(value => reply + value)
        });
        Register(true, "old:");
        var old = Assert.Single(host.GetTools(), t => t.Function.Name == "host_extension");
        Assert.True(old.IsReadOnly);
        Register(false, "new:");
        var native = Assert.Single(host.GetTools(), t => t.Function.Name == "host_extension");
        Assert.Equal(2, changes);
        Assert.False(native.IsReadOnly);
        Assert.Equal("Host extension", native.FriendlyName);
        var starts = new List<AiFunctionStartedEvent>();
        provider.FunctionStarted += (_, e) => starts.Add(e);
        var args = new AIFunctionArguments { ["value"] = "payload" };
        var result = await host.InvokeAsync(native.Function, args, new OneWareInvocation("preserved-session", "preserved-call"), CancellationToken.None);
        Assert.Contains("new:payload", JsonSerializer.Serialize(result));
        Assert.Equal("preserved-session", Assert.Single(starts).SessionId);
        Assert.Equal("preserved-call", starts[0].ToolCallId);
        Assert.Equal("host_extension", starts[0].ToolName);
        provider.RegisterPromptAddition("  Native extension guidance  ");
        Assert.Equal(new[] { "Native extension guidance" }, host.GetPromptAdditions());
        host.ToolsChanged -= changed;
        Register(true, "later:");
        Assert.Equal(2, changes);
        Assert.True(old.IsReadOnly);
    }

    private static (AiFunctionProvider Provider, OneWareHost Host) CreateHost()
    {
        var dock = InterfaceStub.Create<IMainDockService>((name, _) => name == "get_OpenFiles"
            ? new Dictionary<string, IExtendedDocument>() : null);
        var projects = InterfaceStub.Create<IProjectExplorerService>((_, _) => null);
        var provider = new AiFunctionProvider(projects, dock,
            InterfaceStub.Create<IErrorService>((_, _) => null),
            InterfaceStub.Create<ITerminalManagerService>((_, _) => null),
            InterfaceStub.Create<IWindowService>((_, _) => null),
            InterfaceStub.Create<IPaths>((_, _) => null), NullLogger.Instance,
            new AiFileEditService(dock, AcpTestSupport.Settings()));
        return (provider, new OneWareHost(provider, dock, projects));
    }
}
