using System.Text.Json;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private List<(string Session, ChatNotice Notice)>? _pendingNotices;

    private bool ObserveNotice(IAgentConnection connection, long generation, string method, JsonElement data)
    {
        if (method != "session/update" || !data.TryGetProperty("update", out var update) ||
            Text(update, "sessionUpdate") != "notice") return false;
        if (_delegation != null || _disposed || _family.AccountChanging || !ReferenceEquals(connection, _connection) ||
            generation != _generation || !connection.IsConnected || Text(data, "sessionId") is not { } session ||
            _prompt?.Cancellation.IsCancellationRequested == true) return true;
        var severity = Text(update, "severity");
        var title = Text(update, "title");
        if (severity == null || string.IsNullOrWhiteSpace(title)) return true;
        if (update.TryGetProperty("description", out var description) &&
            description.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return true;
        if (update.TryGetProperty("_meta", out var metadata) &&
            metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return true;
        var notice = new ChatNotice(severity, title, Text(update, "description"),
            metadata.ValueKind == JsonValueKind.Object ? metadata.Clone() : null);

        // New sessions and freshly emitted reconnect notices can precede the session reply.
        if (_pendingNotices != null)
        {
            if (_pendingNotices.Any(x => x.Session == session && x.Notice.HasSameContent(notice))) return true;
            if (_pendingNotices.Count == 20) _pendingNotices.RemoveAt(0);
            _pendingNotices.Add((session, notice));
        }
        else if (_loaded && session == _session) PublishNotice(notice);
        return true;
    }

    private void PublishNotice(ChatNotice notice) =>
        Emit(new ChatNoticeEvent(_conversation ?? _session!, notice));

    private void PublishPendingNotices()
    {
        var pending = _pendingNotices;
        _pendingNotices = null;
        if (pending == null) return;
        foreach (var (session, notice) in pending)
            if (session == _session) PublishNotice(notice);
    }

    private void ResetNotices()
    {
        _pendingNotices = null;
        if (_delegation == null) Emit(new ChatNoticesClearedEvent());
    }
}
