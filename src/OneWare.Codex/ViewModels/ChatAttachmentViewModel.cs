using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OneWare.Codex.ViewModels;

/// <summary>
/// A single attachment chip shown in the chat attachment strip. Represents either a whole
/// file, a code selection within a file, or an inline image blob (e.g. pasted from clipboard), and
/// exposes content for the transport to capture when a message is sent.
/// </summary>
public sealed class ChatAttachmentViewModel : ObservableObject, IDisposable
{
    private readonly byte[]? _imageData;
    private readonly string? _mimeType;

    public string FilePath { get; }

    public string DisplayName { get; }

    /// <summary>Short line-range hint shown on the chip, e.g. "L10-24". Null for whole-file attachments.</summary>
    public string? Detail { get; }

    /// <summary>True for the implicit, auto-tracked focused-file chip.</summary>
    public bool IsActiveFile { get; }

    public string IconResourceKey { get; }

    /// <summary>True when this attachment wraps inline image data rather than a file path.</summary>
    public bool IsImage { get; }

    /// <summary>Small decoded thumbnail for image attachments; null for file/selection attachments.</summary>
    public Bitmap? ThumbnailBitmap { get; }

    public IRelayCommand RemoveCommand { get; }

    /// <summary>
    /// Whether the active-file chip is sent with the next message. Other attachments are always included.
    /// </summary>
    public bool IsIncluded
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>Switches <see cref="IsIncluded"/>; only offered for the active-file chip.</summary>
    public IRelayCommand ToggleIncludedCommand => field ??= new RelayCommand(() =>
    {
        IsIncluded = !IsIncluded;
        _onToggled?.Invoke(this);
    });

    private Action<ChatAttachmentViewModel>? _onToggled;

    /// <summary>Called after the user switched <see cref="IsIncluded"/>.</summary>
    public ChatAttachmentViewModel OnToggled(Action<ChatAttachmentViewModel> onToggled)
    {
        _onToggled = onToggled;
        return this;
    }

    public ChatAttachmentViewModel(
        string filePath,
        string displayName,
        bool isActiveFile,
        Action<ChatAttachmentViewModel> onRemove,
        SelectionRange? selection = null,
        string iconResourceKey = "VsImageLib.File16X")
    {
        FilePath = filePath;
        DisplayName = displayName;
        IsActiveFile = isActiveFile;
        IconResourceKey = iconResourceKey;

        if (selection is { } s)
        {
            Detail = s.StartLine == s.EndLine ? $"{s.StartLine}" : $"{s.StartLine}-{s.EndLine}";
        }

        RemoveCommand = new RelayCommand(() => onRemove(this));
    }

    /// <summary>Creates an image-blob attachment from raw bytes (e.g. a clipboard paste).</summary>
    public ChatAttachmentViewModel(
        byte[] imageData,
        string mimeType,
        string displayName,
        bool isActiveFile,
        Action<ChatAttachmentViewModel> onRemove)
    {
        FilePath = string.Empty;
        DisplayName = displayName;
        IsActiveFile = isActiveFile;
        IsImage = true;
        IconResourceKey = "VsImageLib.File16X";
        _imageData = imageData.ToArray();
        _mimeType = mimeType;

        try
        {
            using var ms = new MemoryStream(imageData);
            ThumbnailBitmap = Bitmap.DecodeToWidth(ms, 24);
        }
        catch
        {
            // Best-effort thumbnail; show nothing if decoding fails.
        }

        RemoveCommand = new RelayCommand(() => onRemove(this));
    }

    public ReadOnlyMemory<byte> ImageData => _imageData ?? [];
    public string? MimeType => _mimeType;

    public void Dispose()
    {
        ThumbnailBitmap?.Dispose();
    }

    public readonly record struct SelectionRange(int StartLine, int StartColumn, int EndLine, int EndColumn);
}
