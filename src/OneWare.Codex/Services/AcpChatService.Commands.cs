using System.Text.Json;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Enums;

namespace OneWare.Codex.Services;

internal sealed record AcpCommandChoice(string Name, string Description, string? Hint, string? UnavailableReason)
{
    public string Label => "/" + Name;
    public bool IsSupported => UnavailableReason == null;
}

internal sealed partial class AcpChatService
{
    private sealed record RuntimeCommand(string Name, string Arguments)
    {
        public string Text => "/" + Name + (Arguments.Length == 0 ? "" : " " + Arguments);
    }

    private AcpCommandChoice[] _commands = [];
    private AcpCommandChoice[] _filteredCommands = [];
    private readonly Dictionary<string, JsonElement> _pendingCommands = new(StringComparer.Ordinal);
    private bool _collectingCommands;
    private string? _commandsSession;
    private TaskCompletionSource<bool> _commandsAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _loadingCommands;
    private string? _commandsError;
    private string _commandSearchText = "";
    private string _commandArguments = "";
    private AcpCommandChoice? _selectedCommand;

    public IReadOnlyList<AcpCommandChoice> Commands => _commands;
    public IReadOnlyList<AcpCommandChoice> FilteredCommands => _filteredCommands;
    public bool CanOpenCommands => !_disposed && !_family.AccountChanging && _authKind != "none";
    public bool CanRunCommand => CanOpenCommands && !_loadingCommands && _sending == 0 &&
        SelectedCommand is { IsSupported: true } selected &&
        CommandArgumentError(new RuntimeCommand(selected.Name, CommandArguments.Trim())) == null;
    public string CommandHint => SelectedCommand?.Hint ?? "No arguments";
    public bool CommandHasArguments => SelectedCommand?.Name is "mcp" or "review" or "review-branch" or "review-commit";
    public string CommandsStatus => _loadingCommands ? "Loading commands…" : _commandsError ??
        (_sending != 0 ? "Wait for the current response to finish, or use Stop." :
            SelectedCommand?.UnavailableReason ??
            (SelectedCommand is { } selected
                ? CommandArgumentError(new RuntimeCommand(selected.Name, CommandArguments.Trim()))
                : null) ??
            (_commands.Length == 0 ? "No commands advertised by this session." :
                _sessionRecoveryNotice ?? (SelectedCommand?.Name == "mcp"
                    ? "Leave arguments empty to show status. Use reconnect to reload MCP servers in all open sessions."
                    : "Attachments stay in your draft.")));

    public string CommandSearchText
    {
        get => _commandSearchText;
        set { if (SetProperty(ref _commandSearchText, value)) RefreshCommandProjection(); }
    }

    public string CommandArguments
    {
        get => _commandArguments;
        set { if (SetProperty(ref _commandArguments, value)) RefreshCommandProjection(); }
    }

    public AcpCommandChoice? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (!SetProperty(ref _selectedCommand, value)) return;
            _commandArguments = "";
            _commandsError = null;
            RefreshCommandProjection();
        }
    }

    public async Task RefreshCommandsAsync()
    {
        if (_loadingCommands || !CanOpenCommands || _sending != 0) return;
        _loadingCommands = true;
        _commandsError = null;
        await PublishOptionsAsync();
        try
        {
            await ChangeOptionsAsync(null, null, loadingCommands: true);
            _commandsError = _optionsError;
        }
        finally
        {
            _loadingCommands = false;
            await PublishOptionsAsync();
        }
    }

    public async Task RunSelectedCommandAsync()
    {
        if (!CanRunCommand || SelectedCommand is not { } selected) return;
        var command = new RuntimeCommand(selected.Name, CommandArguments.Trim());
        try { await SendAsync(command.Text, ChatSendMode.Send); }
        catch (Exception error)
        {
            _commandsError = error.Message;
            await PublishOptionsAsync();
        }
    }

    private static bool IsCommandName(string name) => name.Length is > 0 and <= 64 &&
        char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static RuntimeCommand? ParseRuntimeCommand(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return null;
        var end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
        var name = trimmed[1..end];
        // Absolute paths and plugin skill references remain ordinary message content.
        return IsCommandName(name) ? new RuntimeCommand(name.ToLowerInvariant(), trimmed[end..].Trim()) : null;
    }

    private static string? UnsupportedCommandReason(string name) => name switch
    {
        "status" or "skills" or "mcp" or "compact" or "review" or "review-branch" or "review-commit" => null,
        "plan" => "Use OneWare's Agent / Plan selector to change mode.",
        "logout" => "Use the account menu to sign out of all related conversations.",
        "rename" => "Use OneWare's conversation menu to rename this chat.",
        _ => "This command is not yet available in OneWare."
    };

    private static string? CommandArgumentError(RuntimeCommand command) => command.Name switch
    {
        "status" or "skills" or "compact" when command.Arguments.Length != 0 =>
            command.Text.Split(' ')[0] + " does not take arguments.",
        "mcp" when command.Arguments.Length != 0 &&
                   !command.Arguments.Equals("reconnect", StringComparison.OrdinalIgnoreCase) =>
            "Use /mcp to show server status, or /mcp reconnect to reconnect servers.",
        "review-branch" when command.Arguments.Length == 0 => "Enter the base branch to review against.",
        "review-commit" when command.Arguments.Length == 0 => "Enter the commit to review.",
        _ => null
    };

    private async Task ValidateRuntimeCommandAsync(RuntimeCommand command, CancellationToken token)
    {
        try { await _commandsAvailable.Task.WaitAsync(TimeSpan.FromSeconds(3), token); }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The ACP command list is not available yet. Open the / menu and retry.");
        }
        if (_commandsSession != _session || !_commands.Any(x => x.Name == command.Name))
            throw new InvalidOperationException("This ACP session does not advertise /" + command.Name + ".");
        if ((UnsupportedCommandReason(command.Name) ?? CommandArgumentError(command)) is { } error)
            throw new InvalidOperationException(error);
    }

    private void RefreshCommandProjection()
    {
        var filtered = _commands.Where(x => x.Label.Contains(CommandSearchText, StringComparison.OrdinalIgnoreCase) ||
            x.Description.Contains(CommandSearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!_filteredCommands.SequenceEqual(filtered)) _filteredCommands = filtered;
        var selected = _commands.FirstOrDefault(x => x.Name == _selectedCommand?.Name);
        if (selected == null && _selectedCommand != null) _commandArguments = "";
        _selectedCommand = selected;
        foreach (var property in new[] { nameof(Commands), nameof(FilteredCommands), nameof(SelectedCommand),
                     nameof(CanOpenCommands), nameof(CanRunCommand), nameof(CommandHint), nameof(CommandHasArguments),
                     nameof(CommandArguments), nameof(CommandsStatus) })
            OnPropertyChanged(property);
    }

    private void ResetCommandsLocked()
    {
        _commands = [];
        _commandsAvailable.TrySetCanceled();
        _commandsAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandsSession = null;
        _pendingCommands.Clear();
        _collectingCommands = false;
    }

    private void AcceptCommandCatalog(string session, JsonElement catalog)
    {
        _commandsSession = session;
        _commands = catalog.EnumerateArray()
            .Where(x => Text(x, "name") is { } name && IsCommandName(name))
            .Select(x =>
            {
                var name = Text(x, "name")!.ToLowerInvariant();
                return new AcpCommandChoice(name, Text(x, "description") ?? "",
                    x.TryGetProperty("input", out var input) ? Text(input, "hint") : null,
                    UnsupportedCommandReason(name));
            }).DistinctBy(x => x.Name).ToArray();
        _commandsAvailable.TrySetResult(true);
        PostOptions();
    }

    private bool ObserveCommandCatalog(IAgentConnection connection, long generation, string method, JsonElement data)
    {
        if (method != "session/update" || !data.TryGetProperty("update", out var update) ||
            Text(update, "sessionUpdate") != "available_commands_update") return false;
        if (_disposed || !ReferenceEquals(connection, _connection) || generation != _generation ||
            Text(data, "sessionId") is not { } session ||
            !update.TryGetProperty("availableCommands", out var catalog) || catalog.ValueKind != JsonValueKind.Array)
            return true;
        if (_collectingCommands)
        {
            // Agents can advertise before session/new returns its session ID. Bind only the reply's ID.
            if (_pendingCommands.ContainsKey(session) || _pendingCommands.Count < 8)
                _pendingCommands[session] = catalog.Clone();
        }
        else if ((session == _session && _loaded) || session == _commandsSession)
            AcceptCommandCatalog(session, catalog);
        return true;
    }

    private async Task<JsonElement> RequestSessionWithCommandsAsync(string method, object parameters, CancellationToken token)
    {
        var connection = _connection!;
        var generation = _generation;
        lock (_sync)
        {
            ResetCommandsLocked();
            _collectingCommands = true;
        }
        try
        {
            var reply = await connection.RequestAsync(method, parameters, token);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(connection, _connection) || generation != _generation)
                    throw new OperationCanceledException();
                var session = Text(reply, "sessionId") ?? _session;
                if (session != null && (method == "session/new" || session == _session))
                {
                    _commandsSession = session;
                    if (_pendingCommands.TryGetValue(session, out var catalog)) AcceptCommandCatalog(session, catalog);
                }
            }
            return reply;
        }
        finally
        {
            lock (_sync)
            {
                _collectingCommands = false;
                _pendingCommands.Clear();
            }
        }
    }
}
