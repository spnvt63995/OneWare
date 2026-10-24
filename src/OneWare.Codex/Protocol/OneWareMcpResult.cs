using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OneWare.Codex.Protocol;

/// <summary>Encodes original OneWare tool values as MCP content, independent of App Server responses.</summary>
internal static class OneWareMcpResult
{
    public static object Success(object? value)
    {
        var content = value switch
        {
            AIContent item => new[] { item },
            IEnumerable<AIContent> collection => collection,
            _ => null
        };
        var items = content == null ? new[] { Text(JsonSerializer.Serialize(value)) }
            : content.Select(ToContentItem).ToArray();
        return new { isError = false, content = items };
    }

    public static object Failure(string text) => new { isError = true, content = new[] { Text(text) } };

    private static object ToContentItem(AIContent content) => content switch
    {
        TextContent text => Text(text.Text ?? ""),
        DataContent image when image.HasTopLevelMediaType("image") =>
            new { type = "image", mimeType = image.MediaType, data = Convert.ToBase64String(image.Data.Span) },
        // Preserve the existing diagnostic as well as the wire content during extraction.
        _ => throw new NotSupportedException($"Codex cannot return this tool content: {content.GetType().Name}.")
    };

    private static object Text(string text) => new { type = "text", text };
}
