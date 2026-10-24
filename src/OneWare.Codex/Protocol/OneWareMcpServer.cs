using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace OneWare.Codex.Protocol;

internal sealed record OneWareMcpTool(string Name, string? Description, JsonElement InputSchema, bool IsReadOnly);

/// <summary>Authenticated loopback MCP endpoint. The owner validates and dispatches every invocation.</summary>
internal sealed class OneWareMcpServer : IAsyncDisposable
{
    public const string ServerName = "oneware";
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _calls = new();
    private readonly Func<string, JsonElement, JsonElement, CancellationToken, Task<object>> _invoke;
    private readonly string _authorization = "Bearer " + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _baseUrl;
    private readonly Task _accept;
    private Catalog _catalog;
    private long _request;
    private int _disposed;

    private sealed record Catalog(string Path, string Signature, JsonElement Tools);

    public OneWareMcpServer(IReadOnlyCollection<OneWareMcpTool> tools,
        Func<string, JsonElement, JsonElement, CancellationToken, Task<object>> invoke)
    {
        _invoke = invoke;
        _catalog = CreateCatalog(tools);
        // HttpListener has no port-zero binding. Retry only a collision between reservation and bind.
        for (var attempt = 0; ; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _baseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(_baseUrl + "/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 4) { }
        }
        _accept = AcceptAsync();
    }

    internal string Url => _baseUrl + Volatile.Read(ref _catalog).Path;
    internal JsonElement Tools => Volatile.Read(ref _catalog).Tools;
    internal string Authorization => _authorization;

    internal bool Matches(IReadOnlyCollection<OneWareMcpTool> tools) =>
        Volatile.Read(ref _catalog).Signature == CreateCatalog(tools).Signature;

    internal void Publish(IReadOnlyCollection<OneWareMcpTool> tools)
    {
        var next = CreateCatalog(tools);
        if (next.Signature != Volatile.Read(ref _catalog).Signature) Volatile.Write(ref _catalog, next);
    }

    private static Catalog CreateCatalog(IReadOnlyCollection<OneWareMcpTool> tools)
    {
        var list = JsonSerializer.SerializeToElement(tools.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new
        {
            name = t.Name, description = t.Description ?? t.Name, inputSchema = t.InputSchema,
            annotations = new { readOnlyHint = t.IsReadOnly }
        }).ToArray());
        return new Catalog("/mcp/" + Guid.NewGuid().ToString("N"), list.GetRawText(), list);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _request);
                var task = RespondAsync(context);
                _requests[id] = task;
                _ = ForgetAsync(id, task);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task ForgetAsync(long id, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The response was closed; observe transport failures during shutdown. */ }
        finally { _requests.TryRemove(id, out _); }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        JsonElement? id = null;
        try
        {
            if (request.RemoteEndPoint is not { Address: var address } || !IPAddress.IsLoopback(address) ||
                request.Headers["Authorization"] != _authorization || request.Headers["Origin"] != null)
            {
                response.StatusCode = 403;
                return;
            }
            var catalog = Volatile.Read(ref _catalog);
            if (request.Url?.AbsolutePath != catalog.Path) { response.StatusCode = 404; return; }
            if (request.HttpMethod == "DELETE") { response.StatusCode = 204; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; response.Headers["Allow"] = "POST, DELETE"; return; }
            if (request.ContentLength64 > 16 * 1024 * 1024) { response.StatusCode = 413; return; }
            using var document = await JsonDocument.ParseAsync(request.InputStream, cancellationToken: _lifetime.Token).ConfigureAwait(false);
            var message = document.RootElement;
            var method = Text(message, "method");
            if (message.TryGetProperty("id", out var requestId)) id = requestId.Clone();
            var parameters = message.TryGetProperty("params", out var p) ? p : Empty;
            if (id == null)
            {
                if (method == "notifications/cancelled" && parameters.TryGetProperty("requestId", out var cancelled) &&
                    _calls.TryGetValue(catalog.Path + cancelled.GetRawText(), out var pending)) pending.Cancel();
                response.StatusCode = 202;
                return;
            }
            object result;
            switch (method)
            {
                case "initialize":
                    var requested = Text(parameters, "protocolVersion");
                    result = new { protocolVersion = requested == "2025-03-26" ? requested : "2025-06-18",
                        capabilities = new { tools = new { } }, serverInfo = new { name = "OneWare", version = "1" } };
                    break;
                case "ping": result = new { }; break;
                case "tools/list": result = new { tools = catalog.Tools }; break;
                case "resources/list": result = new { resources = Array.Empty<object>() }; break;
                case "resources/templates/list": result = new { resourceTemplates = Array.Empty<object>() }; break;
                case "tools/call":
                    var name = Text(parameters, "name") ?? "";
                    if (!catalog.Tools.EnumerateArray().Any(t => Text(t, "name") == name))
                    {
                        result = OneWareMcpResult.Failure("This OneWare tool is no longer registered. Use the current MCP tool list.");
                        break;
                    }
                    using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                    {
                        var key = catalog.Path + id.Value.GetRawText();
                        if (!_calls.TryAdd(key, cancellation)) throw new InvalidOperationException("Duplicate MCP request.");
                        try
                        {
                            var args = parameters.TryGetProperty("arguments", out var a) ? a.Clone() : Empty;
                            var meta = parameters.TryGetProperty("_meta", out var m) ? m.Clone() : Empty;
                            result = await _invoke(name, args, meta, cancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { result = OneWareMcpResult.Failure("The OneWare tool request was cancelled."); }
                        catch (Exception) { result = OneWareMcpResult.Failure("The OneWare tool request could not complete."); }
                        finally { _calls.TryRemove(key, out _); }
                    }
                    break;
                default:
                    await WriteAsync(response, new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported MCP method." } });
                    return;
            }
            await WriteAsync(response, new { jsonrpc = "2.0", id, result });
        }
        catch (JsonException)
        {
            try { await WriteAsync(response, new { jsonrpc = "2.0", id, error = new { code = -32700, message = "Invalid JSON." } }); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception)
        {
            try { await WriteAsync(response, new { jsonrpc = "2.0", id, error = new { code = -32600, message = "Invalid MCP request." } }); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
        }
        finally { response.Close(); }
    }

    private static async Task WriteAsync(HttpListenerResponse response, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _listener.Close();
        await _accept.ConfigureAwait(false);
        try { await Task.WhenAll(_requests.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _lifetime.Dispose();
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
}
