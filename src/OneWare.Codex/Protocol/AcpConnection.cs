using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OneWare.Codex.Services;

namespace OneWare.Codex.Protocol;

/// <summary>One owned stdio process. The reader never waits for UI/tool replies.</summary>
internal sealed class AcpConnection(AcpProfile profile) : IAgentConnection
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly SemaphoreSlim _write = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _reader;
    private Task? _errors;
    private long _id;
    private long _serverId;
    private int _ended;
    private int _disposed;

    public bool IsConnected => Volatile.Read(ref _ended) == 0 && _process is { HasExited: false };
    public event Action<string, JsonElement>? Notification;
    public event Action<Exception>? Disconnected;
    public Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest { get; set; }

    public Task StartAsync(string executable, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process != null) throw new InvalidOperationException("ACP connection has already been started.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        start.Environment["CODEX_HOME"] = profile.Home;
        if (profile.CodexExecutable != null) start.Environment["CODEX_PATH"] = profile.CodexExecutable;
        start.Environment["CODEX_CONFIG"] = JsonSerializer.Serialize(new
        {
            sandbox_mode = "read-only", approval_policy = "never", web_search = "disabled",
            cli_auth_credentials_store = "file",
            mcp_servers = new { }, model = profile.Model, model_reasoning_effort = profile.Effort,
            features = new
            {
                apps = false, plugins = false, hooks = false, computer_use = false,
                browser_use = false, multi_agent = false, multi_agent_v2 = false
            }
        });
        foreach (var argument in profile.Arguments) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new IOException("Could not start Codex.");
        _reader = ReadAsync();
        // Drain stderr without retaining potentially private config, paths or authentication data.
        _errors = DrainErrorsAsync();
        return Task.CompletedTask;
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (method == "authenticate") deadline.CancelAfter(TimeSpan.FromMinutes(10));
        else if (method != "session/prompt") deadline.CancelAfter(TimeSpan.FromSeconds(60));
        _pending[id] = reply;
        using var registration = deadline.Token.Register(() => reply.TrySetCanceled(deadline.Token));
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token);
            return await reply.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken token)
    {
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsConnected) throw new IOException("The Codex process is disconnected.");
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token)
                .ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process!.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    var parameters = message.TryGetProperty("params", out var p) ? p.Clone() : Empty;
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        var key = Interlocked.Increment(ref _serverId);
                        var task = AnswerAsync(requestId.Clone(), method.GetString()!, parameters);
                        _requests[key] = task;
                        _ = RemoveRequestAsync(key, task);
                    }
                    else Notification?.Invoke(method.GetString()!, parameters);
                }
                // Our outbound IDs are strings. JSON-RPC number 1 cannot answer string "1".
                else if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                         _pending.TryRemove(id.GetString()!, out var pending))
                {
                    if (message.TryGetProperty("error", out var error))
                        pending.TrySetException(new AcpRpcException(error));
                    else if (message.TryGetProperty("result", out var result))
                        pending.TrySetResult(result.Clone());
                    else pending.TrySetException(new IOException("Codex returned a malformed response."));
                }
            }

            End(new IOException("Codex disconnected. Reconnect to resume the conversation."));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            End(exception);
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            if (ServerRequest == null) throw new NotSupportedException("No client handler for " + method);
            var result = await ServerRequest(method, parameters, _lifetime.Token).ConfigureAwait(false);
            await WriteAsync(new { jsonrpc = "2.0", id, result }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_lifetime.IsCancellationRequested) return;
            try
            {
                await WriteAsync(new
                    {
                        jsonrpc = "2.0", id, error = new
                        {
                            code = exception is NotSupportedException ? -32601 : -32603,
                            message = exception is NotSupportedException
                                ? "Unsupported client request: " + method
                                : "Client request could not complete."
                        }
                    },
                    _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!IsConnected)
            {
            }
        }
    }

    private async Task RemoveRequestAsync(long key, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            End(exception);
        }
        finally
        {
            _requests.TryRemove(key, out _);
        }
    }

    private async Task DrainErrorsAsync()
    {
        try
        {
            var buffer = new char[4096];
            while (await _process!.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token)
                       .ConfigureAwait(false) != 0)
            {
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private void End(Exception exception)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _lifetime.Cancel();
        foreach (var (_, completion) in _pending) completion.TrySetException(exception);
        _pending.Clear();
        Disconnected?.Invoke(exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _ended, 1);
        _lifetime.Cancel();
        foreach (var (_, pending) in _pending) pending.TrySetCanceled();
        _pending.Clear();
        if (_process != null)
        {
            try
            {
                _process.StandardInput.Close();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (_reader != null) await _reader.ConfigureAwait(false);
            if (_errors != null) await _errors.ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_requests.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            _process.Dispose();
        }
    }

    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
}

internal sealed class AcpRpcException(JsonElement error)
    : Exception(error.TryGetProperty("message", out var message) ? message.GetString() : "ACP request failed.")
{
    public int Code { get; } = error.TryGetProperty("code", out var code) ? code.GetInt32() : -32603;
}