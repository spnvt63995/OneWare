using System.Text.Json;

namespace OneWare.Essentials.Models;

/// <summary>A live, plain-text advisory. It is neither conversation history nor a request failure.</summary>
public sealed record ChatNotice(string Severity, string Title, string? Description = null, JsonElement? Metadata = null)
{
    public bool HasSameContent(ChatNotice other) =>
        Severity == other.Severity && Title == other.Title && Description == other.Description &&
        Metadata?.GetRawText() == other.Metadata?.GetRawText();
}

public sealed class ChatNoticeEvent(string sessionId, ChatNotice notice) : ChatEvent
{
    public string SessionId { get; } = sessionId;
    public ChatNotice Notice { get; } = notice;
}

/// <summary>The provider retired the connection or identity that owned its live notices.</summary>
public sealed class ChatNoticesClearedEvent : ChatEvent;
