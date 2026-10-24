using Avalonia.Threading;
using Microsoft.Extensions.AI;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

internal sealed record OneWareTool(AIFunction Function, bool IsReadOnly, string? FriendlyName);
internal sealed record OneWareDocument(string? Path, bool IsDirty, bool IsReadOnly);
internal sealed record OneWareContext(string Directory, string? ActiveDocument,
    IReadOnlyList<OneWareDocument> OpenDocuments);

// AiFunctionProvider reads these property names to associate progress and edits with the caller.
internal sealed record OneWareInvocation(string SessionId, string ToolCallId);

/// <summary>
/// Access to the original OneWare registry and editor state. Keeps native names, confirmation
/// messages and function results; transport naming, prompts and approval policy belong to the caller.
/// This boundary stays internal to the Codex module until another provider actually needs it.
/// </summary>
internal sealed class OneWareHost(IAiFunctionProvider functions, IMainDockService dock,
    IProjectExplorerService projects)
{
    public event EventHandler? ToolsChanged
    {
        add => functions.ToolsChanged += value;
        remove => functions.ToolsChanged -= value;
    }

    public IReadOnlyList<OneWareTool> GetTools() => functions.GetTools()
        .Select(f => new OneWareTool(f, functions.IsFunctionReadOnly(f.Name) == true,
            functions.GetFriendlyName(f.Name))).ToArray();

    public IReadOnlyCollection<string> GetPromptAdditions() => functions.GetPromptAdditions();

    public IReadOnlyCollection<OneWareAiAgent> GetAgents() => functions.GetAgents();
    public IReadOnlyCollection<string> GetSkillDirectories() => functions.GetSkillDirectories();

    public Task<OneWareContext> CaptureContextAsync() => Ui(() =>
    {
        var current = dock.CurrentDocument;
        var directory = projects.ActiveProject?.RootFolderPath;
        if (string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(current?.FullPath))
            directory = Path.GetDirectoryName(current.FullPath);
        directory = Path.GetFullPath(directory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Task.FromResult(new OneWareContext(directory, current?.FullPath,
            dock.OpenFiles.Values.Select(x => new OneWareDocument(x.FullPath, x.IsDirty, x.IsReadOnly)).ToArray()));
    });

    public Task<string?> ConfirmationAsync(string toolName, AIFunctionArguments args)
    {
        if (functions.GetConfirmationCheck(toolName) is not { } check) return Task.FromResult<string?>(null);
        return Ui(() => Task.FromResult(check(args)));
    }

    public async Task<object?> InvokeAsync(AIFunction function, AIFunctionArguments args,
        OneWareInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        args.Context = new Dictionary<object, object?> { [typeof(OneWareInvocation)] = invocation };
        token.ThrowIfCancellationRequested();
        return await function.InvokeAsync(args, token);
    }

    public void Cancel(string? sessionId)
    {
        if (sessionId != null) functions.CancelActiveFunctions(sessionId);
    }

    internal static async Task<T> Ui<T>(Func<Task<T>> action) => await Dispatcher.UIThread.InvokeAsync(action);
}
