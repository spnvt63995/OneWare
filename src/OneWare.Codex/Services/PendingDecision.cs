namespace OneWare.Codex.Services;

/// <summary>A cancelled/completed decision cannot later acquire a session grant.</summary>
internal sealed class PendingDecision<T> : IDisposable
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;
    public PendingDecision(CancellationToken token) => _registration = token.Register(() => _completion.TrySetCanceled(token));
    public Task<T> Task => _completion.Task;
    public bool IsPending => !_completion.Task.IsCompleted;
    public bool TryComplete(T answer) => _completion.TrySetResult(answer);
    public void Dispose() => _registration.Dispose();
}
