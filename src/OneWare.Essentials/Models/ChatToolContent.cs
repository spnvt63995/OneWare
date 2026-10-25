using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace OneWare.Essentials.Models;

public enum ChatToolContentKind { Text, Image, Diff, Notice }

/// <summary>A read-only, self-contained result segment. Paths are labels, never file access instructions.</summary>
public sealed record ChatToolContent
{
    public ChatToolContentKind Kind { get; init; }
    public string? Text { get; init; }
    public string? MimeType { get; init; }
    public string? Data { get; init; }
    public string? Path { get; init; }
    public string? OldText { get; init; }
    public string? NewText { get; init; }

    [JsonIgnore] public bool IsImage => Kind == ChatToolContentKind.Image;
    [JsonIgnore] public bool IsDiff => Kind == ChatToolContentKind.Diff;
    [JsonIgnore] public bool IsText => Kind is ChatToolContentKind.Text or ChatToolContentKind.Notice;
    [JsonIgnore] public string Title => IsImage ? $"Image · {MimeType}" : Path ?? "Change segment";

    public static ChatToolContent Notice(string reason) => new() { Kind = ChatToolContentKind.Notice, Text = reason };
}

/// <summary>Limits apply both to incoming results and restored history, before decoding or diffing.</summary>
public static class ChatToolContentLimits
{
    public const int MaxImageBytes = 2 * 1024 * 1024;
    public const int MaxSnapshotImageBytes = 4 * 1024 * 1024;
    public const int MaxHistoryImageBytes = 8 * 1024 * 1024;
    public const int MaxItems = 16;
    public const int MaxDiffCharacters = 32768;

    public static IReadOnlyList<ChatToolContent> Normalize(IEnumerable<ChatToolContent> items,
        int imageBudget = MaxSnapshotImageBytes)
    {
        var result = new List<ChatToolContent>();
        foreach (var item in items)
        {
            if (result.Count == MaxItems)
            {
                result.Add(ChatToolContent.Notice("Additional result segments exceed the preview limit."));
                break;
            }
            switch (item.Kind)
            {
                case ChatToolContentKind.Image:
                    var error = ValidateImage(item, out var bytes, out _, out _);
                    if (error == null && bytes!.Length > imageBudget)
                        error = "Image omitted: the stored preview size limit was reached.";
                    if (error != null) result.Add(ChatToolContent.Notice(error));
                    else
                    {
                        imageBudget -= bytes!.Length;
                        result.Add(new ChatToolContent
                        {
                            Kind = ChatToolContentKind.Image, MimeType = item.MimeType,
                            Data = Convert.ToBase64String(bytes)
                        });
                    }
                    break;
                case ChatToolContentKind.Diff:
                    result.Add(item.NewText == null || !FitsDiff(item.OldText) || !FitsDiff(item.NewText)
                        ? ChatToolContent.Notice($"Change preview unavailable for {Limit(item.Path, 300)}: missing text or segment exceeds the preview limit.")
                        : item with { Path = Limit(item.Path, 300), Data = null, MimeType = null, Text = null });
                    break;
                case ChatToolContentKind.Text:
                case ChatToolContentKind.Notice:
                    result.Add(new ChatToolContent { Kind = item.Kind, Text = Limit(item.Text, 4000) });
                    break;
                default:
                    result.Add(ChatToolContent.Notice("Unsupported tool result."));
                    break;
            }
        }
        return result;
    }

    public static int ImageBytes(IEnumerable<ChatToolContent> items) => items.Where(x => x.IsImage)
        .Sum(x => x.Data == null ? 0 : x.Data.Length / 4 * 3 -
            (x.Data.EndsWith("==", StringComparison.Ordinal) ? 2 : x.Data.EndsWith('=') ? 1 : 0));

    private static bool FitsDiff(string? text) => text == null || text.Length <= MaxDiffCharacters &&
        text.Count(x => x == '\n') < 1000 && text.Split('\n').All(x => x.Length <= 4096);

    public static string? Limit(string? text, int length) => text?.Length > length ? text[..length] + "…" : text;

    public static string? ValidateImage(ChatToolContent item, out byte[]? bytes, out int width, out int height)
    {
        bytes = null;
        width = height = 0;
        if (item.MimeType is not ("image/png" or "image/jpeg"))
            return "Image preview unavailable: only PNG and JPEG are supported.";
        if (string.IsNullOrEmpty(item.Data)) return "Image preview unavailable: no image bytes were provided.";
        if (item.Data.Length > (MaxImageBytes + 2) / 3 * 4)
            return "Image preview unavailable: image exceeds the 2 MiB limit.";
        try { bytes = Convert.FromBase64String(item.Data); }
        catch (FormatException) { return "Image preview unavailable: invalid base64 data."; }
        if (bytes.Length > MaxImageBytes) return "Image preview unavailable: image exceeds the 2 MiB limit.";
        var data = bytes.AsSpan();
        if (item.MimeType == "image/png" && data.Length >= 33 &&
            data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        }
        else if (item.MimeType == "image/jpeg" && data.Length > 4 && data[0] == 255 && data[1] == 216)
        {
            for (var offset = 2; offset + 4 < data.Length;)
            {
                if (data[offset++] != 255) break;
                while (offset < data.Length && data[offset] == 255) offset++;
                if (offset + 3 >= data.Length) break;
                var marker = data[offset++];
                if (marker is 217 or 218) break;
                if (marker is 1 or >= 208 and <= 215) continue;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                if (length < 2 || offset + length > data.Length) break;
                if (marker is 192 or 193 or 194 && length >= 8)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                    break;
                }
                offset += length;
            }
        }
        if (width <= 0 || height <= 0) return "Image preview unavailable: invalid or unsupported image data.";
        if (width > 8192 || height > 8192 || (long)width * height > 16_000_000)
            return "Image preview unavailable: image dimensions exceed the preview limit.";
        return null;
    }
}
