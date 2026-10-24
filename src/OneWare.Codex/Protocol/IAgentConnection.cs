using System.Text.Json;

namespace OneWare.Codex.Protocol;

internal interface IAgentConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<string, JsonElement>? Notification;
    event Action<Exception>? Disconnected;
    Func<string, JsonElement, CancellationToken, Task<object>>? ServerRequest { get; set; }
    Task StartAsync(string executable, string directory, CancellationToken cancellationToken);
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken = default);
    Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default);
}
