using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneWare.Codex.ViewModels;
using OneWare.Essentials.Helpers;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;

namespace OneWare.Codex.Services;

/// <summary>ACP draft content. Captures bytes/text at submission, before a message can wait in the queue.</summary>
internal sealed class AcpAttachments : ObservableObject, IDisposable
{
    private const int MaxTextBytes = 1024 * 1024;
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private readonly IMainDockService? _dock;
    private IEditor? _editor;
    private bool? _included;
    private bool _hadSelection;

    public AcpAttachments(IMainDockService? dock)
    {
        _dock = dock;
        if (dock != null) dock.PropertyChanged += DockChanged;
        Refresh();
    }

    public ObservableCollection<ChatAttachmentViewModel> Items { get; } = [];

    public ChatAttachmentViewModel? ActiveFile
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public IAsyncRelayCommand<Visual?> AddCommand => field ??= new AsyncRelayCommand<Visual?>(AddAsync);

    private async Task AddAsync(Visual? source)
    {
        if (source != null && TopLevel.GetTopLevel(source) is { } owner)
            AddFiles(await StorageProviderHelper.SelectFilesAsync(owner, "Add Attachment", null));
    }

    internal void AddFiles(IEnumerable<string> paths)
    {
        foreach (var value in paths)
        {
            var path = Path.GetFullPath(value);
            if (Items.Any(x => x.FilePath == path)) continue;
            Items.Add(new ChatAttachmentViewModel(path, Path.GetFileName(path), false, Remove));
        }
    }

    public bool TryAddImage(byte[] bytes, string mimeType, string name)
    {
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes || !SupportedImage(mimeType)) return false;
        Items.Add(new ChatAttachmentViewModel(bytes, mimeType, name, false, Remove));
        return true;
    }

    private void Remove(ChatAttachmentViewModel item)
    {
        if (Items.Remove(item)) item.Dispose();
    }

    private void DockChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(IMainDockService.CurrentDocument)) return;
        _included = null;
        Refresh();
    }

    private void SelectionChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        var document = _dock?.CurrentDocument;
        var editor = document as IEditor;
        if (!ReferenceEquals(editor, _editor))
        {
            if (_editor?.Editor != null) _editor.Editor.TextArea.SelectionChanged -= SelectionChanged;
            _editor = editor;
            if (_editor?.Editor != null) _editor.Editor.TextArea.SelectionChanged += SelectionChanged;
            _included = null;
        }

        var selection = editor?.Editor is { } control && !control.TextArea.Selection.IsEmpty;
        if (selection != _hadSelection)
        {
            _included = null;
            _hadSelection = selection;
        }

        ActiveFile?.Dispose();
        ActiveFile = null;
        if (string.IsNullOrEmpty(document?.FullPath)) return;
        ChatAttachmentViewModel.SelectionRange? range = null;
        if (selection)
        {
            var start = editor!.Editor.TextArea.Selection.StartPosition;
            var end = editor.Editor.TextArea.Selection.EndPosition;
            if (end.Line < start.Line || end.Line == start.Line && end.Column < start.Column)
                (start, end) = (end, start);
            range = new(start.Line, start.Column, end.Line, end.Column);
        }

        ActiveFile = new ChatAttachmentViewModel(document.FullPath, Path.GetFileName(document.FullPath), true,
                _ =>
                {
                    _included = false;
                    Refresh();
                }, range)
            { IsIncluded = _included ?? selection };
        ActiveFile.OnToggled(x => _included = x.IsIncluded);
    }

    internal sealed record Snapshot(JsonElement[] Blocks, ChatAttachmentViewModel[] Pending)
    {
        public bool Accepted { get; set; }
    }

    internal Snapshot Capture()
    {
        Refresh();
        var blocks = new List<JsonElement>();
        if (ActiveFile is { IsIncluded: true } active)
            blocks.Add(CaptureItem(active, _hadSelection ? _editor!.Editor.SelectedText : null));
        var pending = Items.ToArray();
        foreach (var item in pending) blocks.Add(CaptureItem(item, null));
        return new Snapshot(blocks.ToArray(), pending);
    }

    private JsonElement CaptureItem(ChatAttachmentViewModel item, string? selection)
    {
        if (item.IsImage)
        {
            return JsonSerializer.SerializeToElement(new
                { type = "image", mimeType = item.MimeType, data = Convert.ToBase64String(item.ImageData.Span) });
        }

        var path = Path.GetFullPath(item.FilePath);
        var uri = new Uri(path).AbsoluteUri;
        if (selection == null && ImageMime(path) is { } mimeType)
        {
            var bytes = ReadBounded(path, MaxImageBytes);
            if (bytes.Length == 0) throw new InvalidOperationException("The image attachment is empty.");
            return JsonSerializer.SerializeToElement(new
                { type = "image", mimeType, data = Convert.ToBase64String(bytes) });
        }

        var text = selection;
        if (text == null)
        {
            var open = _dock?.OpenFiles.Values.OfType<IEditor>().FirstOrDefault(x => x.FullPath == path);
            text = open?.CurrentDocument.Text;
        }

        text ??= new UTF8Encoding(false, true).GetString(ReadBounded(path, MaxTextBytes));
        if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes || text.Contains('\0'))
            throw new InvalidOperationException(
                "Attach a UTF-8 text file up to 1 MiB, or a supported image up to 10 MiB.");
        return JsonSerializer.SerializeToElement(new
        {
            type = "resource",
            resource = new
            {
                uri = selection == null ? uri : uri + "#selection-" + item.Detail,
                mimeType = "text/plain", text
            }
        });
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit)
            throw new InvalidOperationException("The attachment is too large: " + Path.GetFileName(path));
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidOperationException("The attachment grew beyond its size limit.");
            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    private static string? ImageMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif", ".webp" => "image/webp", _ => null
    };

    private static bool SupportedImage(string mime) =>
        mime is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    internal void Accept(Snapshot snapshot)
    {
        snapshot.Accepted = true;
        foreach (var item in snapshot.Pending)
            if (Items.Remove(item))
                item.Dispose();
        _included = null;
        Refresh();
    }

    internal void Restore(Snapshot snapshot)
    {
        if (!snapshot.Accepted) return;
        snapshot.Accepted = false;
        foreach (var item in snapshot.Pending)
        {
            if (!item.IsImage) AddFiles([item.FilePath]);
            else
            {
                TryAddImage(item.ImageData.ToArray(), item.MimeType!, item.DisplayName);
            }
        }
    }

    public void Dispose()
    {
        if (_dock != null) _dock.PropertyChanged -= DockChanged;
        if (_editor?.Editor != null) _editor.Editor.TextArea.SelectionChanged -= SelectionChanged;
        ActiveFile?.Dispose();
        foreach (var item in Items) item.Dispose();
        Items.Clear();
    }
}