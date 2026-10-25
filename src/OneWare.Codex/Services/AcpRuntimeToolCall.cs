using System.Text.Json;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

/// <summary>Bounded display snapshots for a runtime-owned ACP tool; omitted fields retain their value.</summary>
internal sealed class AcpRuntimeToolCall
{
    private string? _input;
    private string? _locations;
    private string? _rawOutput;
    private IReadOnlyList<ChatToolContent>? _content;
    private string? _additionalOutput;
    private bool _standardContent;
    private bool _mcpTool;
    private bool _preferContent;

    public string? Detail => Join(_input, _locations);
    public string? Output => _preferContent
        ? Limit(string.Join('\n', Content?.Where(x => x.Kind == ChatToolContentKind.Text).Select(x => x.Text) ?? []), 4000)
        : _rawOutput;
    public IReadOnlyList<ChatToolContent>? Content => _content == null ? null :
        string.IsNullOrEmpty(_additionalOutput) ? _content :
        [.. _content.Take(ChatToolContentLimits.MaxItems - 2),
            .. _content.Count > ChatToolContentLimits.MaxItems - 2
                ? new[] { ChatToolContent.Notice("Additional segments omitted to retain the final output.") } : [],
            new ChatToolContent { Kind = ChatToolContentKind.Text, Text = _additionalOutput }];

    public void Update(JsonElement update)
    {
        if (Value(update, "rawInput", out var input))
        {
            _input = DescribeInput(input);
            _mcpTool = Text(input, "server") != null && Text(input, "tool") != null;
        }
        if (Value(update, "rawOutput", out var output))
        {
            var raw = output.ToString();
            _rawOutput = raw.Length <= 4000 ? raw : "Tool output exceeds the text preview limit.";
            _preferContent = false;
            _additionalOutput = Value(output, "error", out var error) ? Limit(error.ToString(), 4000) : null;
            // The installed runtime forwards MCP results in this explicit envelope. Do not
            // guess that arbitrary raw JSON is an image, a diff or a filesystem reference.
            if (_mcpTool && !_standardContent && Value(output, "result", out var result) &&
                Value(result, "content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                _content = ChatToolContentLimits.Normalize(blocks.EnumerateArray().Select(ParseBlock));
                _preferContent = true;
            }
        }
        if (Value(update, "content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            if (!Value(update, "rawOutput", out _)) _additionalOutput = null;
            _standardContent = true;
            _content = ChatToolContentLimits.Normalize(content.EnumerateArray().Select(ParseContent));
            _preferContent = true;
        }
        // Retain a separately reported final text/error without replacing the last content
        // snapshot. Image-generator raw envelopes must never appear as base64 JSON dumps.
        if (_content != null && _additionalOutput == null && Value(update, "rawOutput", out var final) &&
            (Text(update, "status") == "failed" && final.ValueKind == JsonValueKind.String ||
             !_preferContent && (final.ValueKind == JsonValueKind.String || _content.All(x => x.Kind == ChatToolContentKind.Text))))
            _additionalOutput = _rawOutput;
        if (Value(update, "locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
            _locations = string.Join('\n', locations.EnumerateArray().Select(x =>
            {
                var path = Text(x, "path");
                if (path == null) return null;
                var line = x.TryGetProperty("line", out var value) && value.ValueKind == JsonValueKind.Number &&
                           value.TryGetInt64(out var number)
                    ? ":" + number : "";
                return Limit(path, 200) + line;
            }).Where(x => x != null).Take(3));
    }

    private static ChatToolContent ParseContent(JsonElement item) => Text(item, "type") switch
    {
        "content" when Value(item, "content", out var block) => ParseBlock(block),
        "diff" => new ChatToolContent
        {
            Kind = ChatToolContentKind.Diff, Path = Text(item, "path"),
            OldText = Text(item, "oldText"), NewText = Text(item, "newText")
        },
        "terminal" => ChatToolContent.Notice($"Terminal output is not available in this preview: {Limit(Text(item, "terminalId"), 200)}"),
        _ => ChatToolContent.Notice($"Unsupported tool result: {Limit(Text(item, "type"), 100) ?? "unknown"}")
    };

    private static ChatToolContent ParseBlock(JsonElement block) => Text(block, "type") switch
    {
        "text" => new ChatToolContent { Kind = ChatToolContentKind.Text, Text = Text(block, "text") },
        "image" => new ChatToolContent
        {
            Kind = ChatToolContentKind.Image, MimeType = Text(block, "mimeType"), Data = Text(block, "data")
        },
        "resource_link" => ChatToolContent.Notice($"Resource link (no embedded preview): {Limit(Text(block, "name") ?? Text(block, "uri"), 300)}"),
        _ => ChatToolContent.Notice($"Unsupported content: {Limit(Text(block, "type"), 100) ?? "unknown"}")
    };

    private static string? DescribeInput(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? string.Join('\n', value.EnumerateObject().Take(3)
            .Select(x => Limit(x.Name, 64) + ": " + Limit(x.Value.ToString(), 200)))
        : Limit(value.ToString(), 600);

    private static bool Value(JsonElement value, string key, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result) &&
               result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static string? Text(JsonElement value, string key) =>
        Value(value, key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private static string? Limit(string? text, int length) => text?.Length > length ? text[..length] + "…" : text;
    private static string? Join(string? first, string? second) => string.IsNullOrWhiteSpace(first) ? second :
        string.IsNullOrWhiteSpace(second) ? first : first + "\n" + second;
}
