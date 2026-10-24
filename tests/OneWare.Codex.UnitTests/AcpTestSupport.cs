using System.ComponentModel;
using AvaloniaEdit.Editing;
using OneWare.Essentials.EditorExtensions;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;
using OneWare.Settings;

namespace OneWare.Codex.UnitTests;

internal static class AcpTestSupport
{
    internal const string RetiredIntentKey = "Codex_OptionIntentV1";
    internal static SettingsService Settings()
    {
        var settings = new SettingsService();
        // Keep preservation coverage for data written by the retired provider.
        settings.Register(RetiredIntentKey, "");
        return settings;
    }
    internal sealed class Profile : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oneware-options-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "Settings.json");
        public Profile() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1cAAAAASUVORK5CYII=");
    internal sealed class EditorContext
    {
        private PropertyChangedEventHandler? _changed;
        private IExtendedDocument? _current;
        public string Path { get; }
        public ExtendedTextEditor Editor { get; }
        public IEditor Document { get; }
        public IMainDockService Dock { get; }
        public int TrackingSubscribers => _changed?.GetInvocationList().Length ?? 0;

        public EditorContext(string text, string? path = null)
        {
            Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selection.cs");
            Editor = new ExtendedTextEditor { Text = text };
            Document = InterfaceStub.Create<IEditor>((method, _) => method switch
            {
                "get_Editor" => Editor,
                "get_FullPath" => Path,
                "get_CurrentDocument" => Editor.Document,
                "get_IsDirty" => true,
                "get_IsReadOnly" or "get_IsLoading" or "get_LoadingFailed" => false,
                _ => null
            });
            _current = Document;
            Dock = InterfaceStub.Create<IMainDockService>((method, args) =>
            {
                if (method == "get_CurrentDocument") return _current;
                if (method == "get_OpenFiles") return new Dictionary<string, IExtendedDocument> { [Path] = Document };
                if (method == "add_PropertyChanged") _changed += (PropertyChangedEventHandler)args![0]!;
                if (method == "remove_PropertyChanged") _changed -= (PropertyChangedEventHandler)args![0]!;
                return null;
            });
        }
        public void Select(int offset, int length, bool reverse = false) =>
            Editor.TextArea.Selection = Selection.Create(Editor.TextArea,
                reverse ? offset + length : offset, reverse ? offset : offset + length);
        public void Focus(IExtendedDocument? document)
        {
            _current = document;
            _changed?.Invoke(Dock, new PropertyChangedEventArgs(nameof(IMainDockService.CurrentDocument)));
        }
    }
}
