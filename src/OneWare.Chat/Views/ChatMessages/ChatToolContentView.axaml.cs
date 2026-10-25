using Avalonia.Controls;
using Avalonia.Media.Imaging;
using OneWare.Essentials.Controls;
using OneWare.Essentials.Helpers;
using OneWare.Essentials.Models;

namespace OneWare.Chat.Views.ChatMessages;

/// <summary>Displays only the supplied snapshot. Never opens a path, fetches a URI or edits a document.</summary>
public partial class ChatToolContentView : UserControl
{
    private Bitmap? _bitmap;
    private bool _attached;

    public ChatToolContentView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { _attached = true; Refresh(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Release(); };
        DataContextChanged += (_, _) => Refresh();
        DiffExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && DiffExpander.IsExpanded) ShowDiff();
        };
    }

    private void Refresh()
    {
        Release();
        if (!_attached || DataContext is not ChatToolContent item) return;
        if (item.IsImage)
        {
            var error = ChatToolContentLimits.ValidateImage(item, out var bytes, out var width, out var height);
            if (error == null)
            {
                try
                {
                    using var stream = new MemoryStream(bytes!);
                    _bitmap = width >= height ? Bitmap.DecodeToWidth(stream, Math.Min(width, 1024))
                        : Bitmap.DecodeToHeight(stream, Math.Min(height, 1024));
                    Thumbnail.Source = FullImage.Source = _bitmap;
                }
                catch (Exception) { error = "Image preview unavailable: the image could not be decoded."; }
            }
            ImageError.Text = error;
            ImageError.IsVisible = error != null;
            ImageExpander.IsVisible = error == null;
        }
        if (DiffExpander.IsExpanded) ShowDiff();
    }

    private void ShowDiff()
    {
        if (!_attached || DiffPreview.Content != null || DataContext is not ChatToolContent { IsDiff: true } item) return;
        var safe = ChatToolContentLimits.Normalize([item])[0];
        if (!safe.IsDiff)
        {
            DiffPreview.Content = new TextBlock { Text = safe.Text };
            return;
        }
        DiffPreview.Content = new ComparisonControl
        {
            Height = 220,
            Chunks = DiffHelper.BuildDiff(safe.OldText ?? "", safe.NewText ?? "")
        };
    }

    private void Release()
    {
        Thumbnail.Source = FullImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        DiffPreview.Content = null;
    }
}
