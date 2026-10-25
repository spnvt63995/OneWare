using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneWare.Essentials.Models;

namespace OneWare.Chat.ViewModels;

public sealed class ChatNoticesViewModel : ObservableObject
{
    public const int Capacity = 20;
    public ObservableCollection<ChatNoticeViewModel> Entries { get; } = [];
    public bool HasEntries => Entries.Count > 0;

    public ChatNoticesViewModel() =>
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEntries));

    internal void Add(ChatNotice notice)
    {
        if (Entries.Any(x => x.Notice.HasSameContent(notice))) return;
        if (Entries.Count == Capacity) Entries.RemoveAt(0);
        Entries.Add(new ChatNoticeViewModel(notice, x => Entries.Remove(x)));
    }

    internal void Clear() => Entries.Clear();
}

public sealed class ChatNoticeViewModel
{
    public ChatNotice Notice { get; }
    public bool IsWarning => Notice.Severity == "warning";
    public bool IsError => Notice.Severity == "error";
    public bool HasDescription => !string.IsNullOrEmpty(Notice.Description);
    public string SeverityText => Notice.Severity switch
    {
        "info" => "Info", "warning" => "Warning", "error" => "Error", _ => "Notice"
    };
    public string DismissLabel => "Dismiss " + Notice.Title;
    public IRelayCommand DismissCommand { get; }

    internal ChatNoticeViewModel(ChatNotice notice, Action<ChatNoticeViewModel> dismiss)
    {
        Notice = notice;
        DismissCommand = new RelayCommand(() => dismiss(this));
    }
}
