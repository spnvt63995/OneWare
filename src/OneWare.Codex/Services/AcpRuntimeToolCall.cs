using System.Text.Json;

namespace OneWare.Codex.Services;

/// <summary>Bounded display snapshots for a runtime-owned ACP tool; omitted fields retain their value.</summary>
internal sealed class AcpRuntimeToolCall
{
    private string? _input;
    private string? _locations;
    private string? _rawOutput;
    private string? _content;
    private bool _preferContent;

    public string? Detail => Join(_input, _locations);
    public string? Output => _preferContent ? _content : _rawOutput;

    public void Update(JsonElement update)
    {
        if (Value(update, "rawInput", out var input)) _input = DescribeInput(input);
        if (Value(update, "rawOutput", out var output))
        {
            _rawOutput = Limit(output.ToString(), 4000);
            _preferContent = false;
        }
        if (Value(update, "content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            // Content is a replacement snapshot. Images, diffs and terminal references are not text results.
            var texts = new List<string>();
            var length = 0;
            foreach (var item in content.EnumerateArray())
            {
                if (Text(item, "type") != "content" || !Value(item, "content", out var block) ||
                    Text(block, "type") != "text" || Text(block, "text") is not { } text) continue;
                texts.Add(Limit(text, 4000)!);
                length += text.Length;
                if (length >= 4000) break;
            }
            _content = Limit(string.Join('\n', texts), 4000);
            _preferContent = !string.IsNullOrWhiteSpace(_content);
        }
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
