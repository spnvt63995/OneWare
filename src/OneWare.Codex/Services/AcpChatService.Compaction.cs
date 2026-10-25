using System.Text.Json;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private readonly Dictionary<string, string> _compactions = new(StringComparer.Ordinal);
    private List<(string Session, JsonElement Update)>? _pendingCompactions;

    private bool ObserveCompaction(IAgentConnection connection, long generation, string method, JsonElement data)
    {
        if (method != "session/update" || !data.TryGetProperty("update", out var update) ||
            Text(update, "sessionUpdate") is not ("compaction_update" or "compaction_summary_chunk")) return false;
        if (_delegation != null || _disposed || !ReferenceEquals(connection, _connection) ||
            generation != _generation || Text(data, "sessionId") is not { } session ||
            _prompt?.Cancellation.IsCancellationRequested == true) return true;

        // Session creation and replay can deliver entities before the request returns its ID.
        if (_pendingCompactions != null) _pendingCompactions.Add((session, update.Clone()));
        else if (_loaded && session == _session) AcceptCompaction(update);
        return true;
    }

    private void PublishPendingCompactions()
    {
        var pending = _pendingCompactions;
        _pendingCompactions = null;
        if (pending == null) return;
        foreach (var (session, update) in pending)
            if (session == _session) AcceptCompaction(update);
    }

    private void AcceptCompaction(JsonElement update)
    {
        if (Text(update, "compactionId") is not { Length: > 0 } id) return;
        var chunk = Text(update, "sessionUpdate") == "compaction_summary_chunk";
        var status = Text(update, "status");
        if (!chunk && string.IsNullOrEmpty(status)) return;
        IReadOnlyList<JsonElement>? summary = null;
        var hasSummary = chunk || update.TryGetProperty("summary", out _);
        if (chunk)
        {
            if (!update.TryGetProperty("content", out var content) || !IsSummaryBlock(content)) return;
            summary = [content.Clone()];
        }
        else if (hasSummary)
        {
            var value = update.GetProperty("summary");
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.EnumerateArray().Any(x => !IsSummaryBlock(x))) return;
                summary = value.EnumerateArray().Select(x => x.Clone()).ToArray();
            }
            else if (value.ValueKind != JsonValueKind.Null) return;
        }

        var hasError = !chunk && update.TryGetProperty("error", out _);
        if (hasError && update.GetProperty("error").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return;
        // A chunk's metadata belongs to that chunk, not to the compaction entity.
        var hasMetadata = !chunk && update.TryGetProperty("_meta", out _);
        JsonElement? metadata = null;
        if (hasMetadata)
        {
            var value = update.GetProperty("_meta");
            if (value.ValueKind == JsonValueKind.Object) metadata = value.Clone();
            else if (value.ValueKind != JsonValueKind.Null) return;
        }

        _compactions[id] = status ?? _compactions.GetValueOrDefault(id, "in_progress");
        if (_prompt is { } turn) turn.HasCompaction = true;
        Emit(new ChatCompactionEvent(_conversation ?? _session!, id)
        {
            Status = status, HasSummary = hasSummary, AppendSummary = chunk, Summary = summary,
            HasError = hasError, Error = Text(update, "error"), HasMetadata = hasMetadata, Metadata = metadata
        });
    }

    private static bool IsSummaryBlock(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && Text(value, "type") is { Length: > 0 };

    private void InterruptCompactions()
    {
        if (_delegation != null || _session == null) return;
        foreach (var (id, status) in _compactions)
            if (status == "in_progress")
                Emit(new ChatCompactionEvent(_conversation ?? _session, id) { IsInterrupted = true });
    }
}
