using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private Control? _accountView;
    private ChatServiceBlocker? _blocker;
    private CancellationTokenSource? _accountOperation;
    private bool _hasAccountApi;
    private bool _supportsLogin;
    private bool _supportsLogout;
    private bool _runtimeBlocked;
    private bool _runtimeUpdate;
    private string? _authKind;
    private string _accountLabel = "Account status has not been reported";
    private string _accountDetails = "";
    private string _accountMessage = "";
    private long _authRevision;

    public Control HeaderUiExtension => _accountView ??= new Views.AcpChatHeaderView { DataContext = this };
    public ChatServiceBlocker? Blocker { get => _blocker; private set => SetProperty(ref _blocker, value); }
    public string AccountStatusText => _accountLabel;
    public string AccountDetailsText => _accountDetails;
    public string AccountMessage => _accountMessage;
    public bool HasAccountDetails => !string.IsNullOrEmpty(_accountDetails);
    public bool HasAccountMessage => !string.IsNullOrEmpty(_accountMessage);
    public bool IsAuthenticated => _authKind is "account" or "api_key" or "gateway" or "external";
    public bool IsExternalAccount => _authKind is "api_key" or "gateway" or "external";
    public bool IsSigningIn => _accountOperation != null && _accountAction == "login";
    public bool CanSignIn => !_disposed && !_family.AccountChanging && _ready && _supportsLogin && _authKind == "none";
    public bool CanSignOut => !_disposed && !_family.AccountChanging && _ready && _supportsLogout && _authKind == "account";
    public bool CanRefreshAccount => !_disposed && !_family.AccountChanging;
    public IAsyncRelayCommand<Control?> SignInCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Control?> SignOutCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Control?> InstallRuntimeCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Control?> RefreshAccountCommand { get; private set; } = null!;
    public IRelayCommand<Control?> CancelSignInCommand { get; private set; } = null!;
    private string? _accountAction;

    private void InitializeAccount()
    {
        SignInCommand = new AsyncRelayCommand<Control?>(_ => ChangeAccountAsync("login", null), _ => CanSignIn);
        SignOutCommand = new AsyncRelayCommand<Control?>(_ => ChangeAccountAsync("logout", null), _ => CanSignOut);
        InstallRuntimeCommand = new AsyncRelayCommand<Control?>(owner => ChangeAccountAsync("install", owner),
            _ => !_disposed && !_family.AccountChanging && _family.Runtime != null);
        RefreshAccountCommand = new AsyncRelayCommand<Control?>(async _ =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_connection?.IsConnected == true && _hasAccountApi)
                    await ReadAccountAsync(_connection, _generation, _lifetime.Token);
            }
            catch (Exception) { _accountMessage = "Could not refresh the account. Reconnect and try again."; }
            finally { _gate.Release(); }
            await InitializeAsync();
            await AccountUi(PublishAccount);
        }, _ => CanRefreshAccount);
        CancelSignInCommand = new RelayCommand<Control?>(_ => _accountOperation?.Cancel(), _ => IsSigningIn);
    }

    private async Task<bool> CheckRuntimeAsync()
    {
        if (_family.Runtime is not { } runtime) return true;
        _runtimeUpdate = runtime.IsInstalled && await runtime.NeedsUpdateAsync();
        _runtimeBlocked = !runtime.IsInstalled || _runtimeUpdate;
        await AccountUi(PublishAccount);
        return !_runtimeBlocked;
    }

    private async Task InitializeAccountAsync(IAgentConnection connection, long generation, JsonElement result,
        CancellationToken token)
    {
        _supportsLogin = result.TryGetProperty("authMethods", out var methods) &&
                         methods.EnumerateArray().Any(x => Text(x, "id") == "chat-gpt");
        _supportsLogout = _capabilities.TryGetProperty("auth", out var auth) && auth.TryGetProperty("logout", out _);
        _hasAccountApi = _capabilities.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("authStatus", out _);
        // Older/custom ACP agents remain usable; do not invent account calls they never advertised.
        if (_hasAccountApi) await ReadAccountAsync(connection, generation, token);
        await AccountUi(PublishAccount);
    }

    private async Task ReadAccountAsync(IAgentConnection connection, long generation, CancellationToken token)
    {
        var revision = Interlocked.Read(ref _authRevision);
        var result = await connection.RequestAsync("authentication/status", new { }, token);
        await AccountUi(() =>
        {
            if (!AccountConnectionCurrent(connection, generation) || revision != _authRevision) return;
            var type = Text(result, "type");
            // The status push includes the plan; the legacy status read only has an email.
            // Keep the richer identity when a refresh confirms the same account.
            if (type == "chat-gpt" && _authKind == "account" &&
                !string.IsNullOrEmpty(Text(result, "email")) &&
                _accountDetails.Split(" · ")[0] == Text(result, "email")) return;
            var previousIdentity = (_authKind, _accountDetails);
            _authKind = type switch
            {
                "unauthenticated" => "none", "chat-gpt" => "account", "api-key" => "api_key",
                "gateway" => "gateway", _ => null
            };
            _accountLabel = _authKind switch
            {
                "none" => "Not signed in to Codex", "account" => "Signed in with ChatGPT",
                "api_key" => "OpenAI API key", "gateway" => Text(result, "name") ?? "External provider",
                _ => "Account status has not been reported"
            };
            _accountDetails = Text(result, "email") ?? "";
            if (previousIdentity != (_authKind, _accountDetails))
                lock (_sync) ResetNotices();
            PublishAccount();
        });
    }

    private Task ApplyAccountAsync(IAgentConnection connection, long generation, JsonElement status) =>
        AccountUi(() =>
        {
            if (!AccountConnectionCurrent(connection, generation)) return;
            Interlocked.Increment(ref _authRevision);
            var previousIdentity = (_authKind, _accountDetails);
            _authKind = Text(status, "kind");
            _accountLabel = Text(status, "label") ?? "Account status has not been reported";
            _accountDetails = status.TryGetProperty("account", out var account)
                ? string.Join(" · ", new[] { Text(account, "email"), Text(account, "plan") }
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
                : Text(status, "detail") ?? "";
            if (previousIdentity != (_authKind, _accountDetails))
                lock (_sync) ResetNotices();
            PublishAccount();
        });

    private bool AccountConnectionCurrent(IAgentConnection connection, long generation) =>
        !_disposed && ReferenceEquals(_connection, connection) && generation == _generation;

    private void PublishAccount()
    {
        foreach (var name in new[] { nameof(AccountStatusText), nameof(AccountDetailsText), nameof(AccountMessage),
                     nameof(HasAccountDetails), nameof(HasAccountMessage), nameof(IsAuthenticated),
                     nameof(IsExternalAccount), nameof(IsSigningIn), nameof(CanSignIn), nameof(CanSignOut),
                     nameof(CanRefreshAccount) })
            OnPropertyChanged(name);
        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged();
        RefreshAccountCommand.NotifyCanExecuteChanged();
        CancelSignInCommand.NotifyCanExecuteChanged();
        PostOptions();
        if (_family.AccountChanging)
            Blocker = new ChatServiceBlocker(IsSigningIn ? "Sign in to Codex" : "Updating Codex ACP",
                IsSigningIn ? "Complete sign-in in your browser, or cancel to return." : "Please wait…")
            {
                CanRefresh = false, ActionText = IsSigningIn ? "Cancel sign-in" : null,
                ActionCommand = IsSigningIn ? CancelSignInCommand : null
            };
        else if (_runtimeBlocked)
            Blocker = new ChatServiceBlocker(_runtimeUpdate ? "Update Codex ACP" : "Install Codex ACP",
                "Install the Codex ACP runtime through OneWare to start chatting.")
            {
                ActionText = _runtimeUpdate ? "Update Codex ACP" : "Install Codex ACP",
                ActionCommand = InstallRuntimeCommand
            };
        else if (_authKind == "none")
            Blocker = new ChatServiceBlocker("Sign in to Codex", "Connect your ChatGPT account to use Codex ACP.")
            {
                ActionText = _supportsLogin ? "Sign in with ChatGPT" : null,
                ActionCommand = _supportsLogin ? SignInCommand : null
            };
        else Blocker = null;
        StatusChanged?.Invoke(this, new StatusEvent(_ready && Blocker == null,
            Blocker?.Title ?? (_ready ? "Codex ACP ready" : "Codex ACP disconnected")));
    }

    private async Task ChangeAccountAsync(string action, Control? owner)
    {
        if (!await _family.AccountGate.WaitAsync(0)) return;
        AcpChatService[] members;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_family.Sync)
        {
            _family.AccountChanging = true;
            members = _family.Members.ToArray();
        }
        _accountOperation = operation;
        _accountAction = action;
        _accountMessage = "";
        try
        {
            foreach (var member in members)
            {
                await member.AccountUi(member.PublishAccount);
                await member.AbortAsync();
                await member.RetireBackgroundDelegationsAsync();
                await member._gate.WaitAsync(operation.Token);
                try { await member.DisconnectAsync(); }
                finally { member._gate.Release(); }
            }
            operation.Token.ThrowIfCancellationRequested();
            // Account changes still retire user grants, after every in-flight prompt is stopped.
            if (action is "login" or "logout")
                lock (_family.Approvals) _family.Approvals.Clear();
            if (action == "install")
            {
                if (_family.Runtime == null) return;
                if (!await _family.Runtime.InstallAsync(owner)) _accountMessage = "Installation was cancelled. You can retry.";
            }
            else
            {
                await _gate.WaitAsync(operation.Token);
                try
                {
                    await ConnectAsync(operation.Token);
                    if (action == "login" && !_supportsLogin || action == "logout" && !_supportsLogout)
                        throw new NotSupportedException();
                    await _connection!.RequestAsync(action == "login" ? "authenticate" : "logout",
                        action == "login" ? new { methodId = "chat-gpt" } : (object)new { }, operation.Token);
                    await ReadAccountAsync(_connection, _generation, operation.Token);
                }
                finally { _gate.Release(); }
                // Account switching starts fresh conversations. Persisted history/bindings stay intact.
                foreach (var member in members.Where(x => !x._disposed)) await member.NewChatAsync();
            }
        }
        catch (OperationCanceledException) { _accountMessage = "Sign-in cancelled."; }
        catch (Exception)
        {
            _accountMessage = action == "install"
                ? "Installation did not complete. Check your connection and retry."
                : "The account operation did not complete. Please try again.";
        }
        finally
        {
            // Closing the ACP process also closes its browser-login callback and rejects late replies.
            await _gate.WaitAsync();
            try { await DisconnectAsync(); }
            finally { _gate.Release(); }
            _accountOperation = null;
            _accountAction = null;
            lock (_family.Sync) _family.AccountChanging = false;
            _family.AccountGate.Release();
            foreach (var member in members.Where(x => !x._disposed))
            {
                await member.InitializeAsync();
                await member.AccountUi(member.PublishAccount);
            }
        }
    }

    private Task AccountUi(Action action) => OneWareHost.Ui(() =>
    {
        action();
        return Task.FromResult(true);
    });
}
