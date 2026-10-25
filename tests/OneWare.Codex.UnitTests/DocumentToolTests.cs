using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using OneWare.Codex.Services;
using OneWare.Essentials.Extensions;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class DocumentToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oneware-codex-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, IExtendedDocument> _open = [];
    private readonly OneWareHost _host;
    private string FilePath => Path.Combine(_directory, "document.txt");

    public DocumentToolTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "original");
        var functions = InterfaceStub.Create<IAiFunctionProvider>((method, _) => method == "GetTools" ? new List<AIFunction>() : null);
        var dock = InterfaceStub.Create<IMainDockService>((method, args) => method switch
        {
            "get_OpenFiles" => _open,
            "OpenFileAsync" => Task.FromResult<IExtendedDocument?>(_open.GetValueOrDefault(((string)args![0]!).ToPathKey())),
            _ => null
        });
        var projects = InterfaceStub.Create<IProjectExplorerService>((_, _) => null);
        _host = new OneWareHost(functions, dock, projects);
    }
    private TextDocument Open(string text = "original", bool readOnly = false)
    {
        var document = new TextDocument(text);
        var editor = InterfaceStub.Create<IEditor>((method, _) => method switch
        {
            "get_CurrentDocument" => document,
            "get_FullPath" => FilePath,
            "get_IsReadOnly" => readOnly,
            "get_IsLoading" or "get_LoadingFailed" => false,
            "get_IsDirty" => document.Text != File.ReadAllText(FilePath),
            "get_Undo" => new RelayCommand(() => document.UndoStack.Undo(), () => document.UndoStack.CanUndo),
            "SaveAsync" => Save(),
            _ => null
        });
        Task<bool> Save() { File.WriteAllText(FilePath, document.Text); return Task.FromResult(true); }
        _open[FilePath.ToPathKey()] = editor;
        return document;
    }
    [AvaloniaFact]
    public async Task TerminalExecutionLeavesUnsavedDocumentUntouched()
    {
        var document = Open("unsaved");
        var invoked = false;
        var function = AIFunctionFactory.Create(() => invoked = true, "runTerminalCommand");
        await _host.InvokeAsync(function, new AIFunctionArguments(),
            new OneWareInvocation("session", "call"), CancellationToken.None);
        Assert.True(invoked);
        Assert.Equal("unsaved", document.Text);
        Assert.Equal("original", File.ReadAllText(FilePath));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
