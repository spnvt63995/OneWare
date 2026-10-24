using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private sealed class SessionFamily
    {
        public object Sync { get; } = new();
        public HashSet<AcpChatService> Members { get; } = [];
        // Like Copilot's connection owner, keep approvals in memory by conversation, shared
        // by sibling views. Transport replacement and closing a view do not end the chat.
        public Dictionary<string, HashSet<string>> Approvals { get; } = new(StringComparer.Ordinal);
        public bool Closing { get; set; }
        public bool AccountChanging { get; set; }
        public SemaphoreSlim AccountGate { get; } = new(1);
        public AcpRuntime? Runtime { get; init; }
    }

    private readonly SessionFamily _family;
    private readonly bool _ownsFamily;
    private readonly IMainDockService? _dock;
    private Task? _disposal;

    private bool IsApprovedInChat(string session, IReadOnlyList<string> keys)
    {
        lock (_family.Approvals)
            return _family.Approvals.TryGetValue(session, out var approved) && keys.All(approved.Contains);
    }

    private void ApproveInChat(string session, IReadOnlyList<string> keys)
    {
        lock (_family.Approvals)
        {
            if (!_family.Approvals.TryGetValue(session, out var approved))
                _family.Approvals[session] = approved = new HashSet<string>(StringComparer.Ordinal);
            approved.UnionWith(keys);
        }
    }

    public IChatService CreateSession()
    {
        lock (_family.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed || _family.Closing, this);
            // Share the profile/authentication and native tool registry, but give each chat its
            // own transport: ACP cancellation retires a connection to reject late prompt events.
            return new AcpChatService(_host, _profile, _agent, _factory, _dock, _settings, _agents,
                _family, false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_family.Sync)
        {
            if (_disposal != null) return new ValueTask(_disposal);
            if (_ownsFamily) _family.Closing = true;
            PreserveDelegationsBeforeRetirement();
            lock (_sync) _disposed = true;
            var children = _ownsFamily ? _family.Members.Where(x => !ReferenceEquals(x, this)).ToArray() : [];
            return new ValueTask(_disposal = DisposeFamilyMemberAsync(children));
        }
    }

    private async Task DisposeFamilyMemberAsync(AcpChatService[] children)
    {
        try
        {
            // The registered provider owns application shutdown; closing one chat owns only itself.
            await Task.WhenAll(children.Select(x => x.DisposeAsync().AsTask()));
        }
        finally
        {
            try { await DisposeCoreAsync(); }
            finally
            {
                if (_ownsFamily)
                    lock (_family.Approvals) _family.Approvals.Clear();
                lock (_family.Sync) _family.Members.Remove(this);
            }
        }
    }
}
