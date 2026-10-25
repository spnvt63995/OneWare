using System.Text.Json;

namespace OneWare.Essentials.Models;

/// <summary>A persistent, session-scoped report of context compaction.</summary>
public sealed record ChatCompactionState(string SessionId, string Id, string Status = "in_progress")
{
    public IReadOnlyList<JsonElement>? Summary { get; init; }
    public string? Error { get; init; }
    public JsonElement? Metadata { get; init; }
    // Local observation ended without a terminal status; never invent a successful completion.
    public bool IsInterrupted { get; init; }
}

/// <summary>
/// An in-place patch. Omitted fields are retained; present null fields clear them.
/// Summary chunks append, while a summary snapshot replaces all accumulated blocks.
/// </summary>
public sealed class ChatCompactionEvent(string sessionId, string compactionId) : ChatEvent
{
    public string SessionId { get; } = sessionId;
    public string CompactionId { get; } = compactionId;
    public string? Status { get; init; }
    public bool HasSummary { get; init; }
    public bool AppendSummary { get; init; }
    public IReadOnlyList<JsonElement>? Summary { get; init; }
    public bool HasError { get; init; }
    public string? Error { get; init; }
    public bool HasMetadata { get; init; }
    public JsonElement? Metadata { get; init; }
    public bool IsInterrupted { get; init; }
}
