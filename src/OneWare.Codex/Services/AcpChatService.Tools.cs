namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private CancellationTokenSource? _toolsRefreshCancellation;

    private void OnToolsChanged(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (_disposed) return;
            Interlocked.Increment(ref _toolsRevision);
            _toolsRefreshCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _toolsRefreshCancellation = cancellation;
            _ = RefreshToolsAsync(_submissionEpoch, cancellation);
        }
    }

    private async Task RefreshToolsAsync(long epoch, CancellationTokenSource cancellation)
    {
        var entered = false;
        try
        {
            // Match Copilot's registration debounce. The pinned ACP agent has no SetTools API;
            // MCP list_changed is only logged by its Codex client. Resume the owned session at
            // the next prompt boundary, including idle chats without another user submission.
            await Task.Delay(250, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            entered = true;
            lock (_sync)
                if (_disposed || epoch != _submissionEpoch || _family.AccountChanging ||
                    _session == null || _connection == null) return;
            if (Path.GetFullPath((await _host.CaptureContextAsync()).Directory) != _cwd) return;
            var (tools, _) = await CaptureToolsAsync();
            // A new registry event may supersede the debounce, but must not cancel an ACP
            // resume halfway through. The next worker will publish the newer snapshot.
            await EnsureSessionAsync(_cwd!, tools, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed && !_lifetime.IsCancellationRequested)
                Failure(new IOException("Could not refresh ACP tools. Send again to retry.", error));
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_sync)
            {
                if (ReferenceEquals(_toolsRefreshCancellation, cancellation)) _toolsRefreshCancellation = null;
                cancellation.Dispose();
            }
        }
    }
}
