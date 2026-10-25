using Avalonia.Threading;
using OneWare.Essentials.Models;

namespace OneWare.Chat.ViewModels;

public partial class ChatSessionViewModel
{
    public ChatNoticesViewModel Notices { get; } = new();
    private long _noticeGeneration;

    private void ReceiveNotice(object? sender, ChatNoticeEvent update)
    {
        var generation = Interlocked.Read(ref _noticeGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Interlocked.Read(ref _noticeGeneration) || !ReferenceEquals(Service, sender) ||
                CurrentSessionId != update.SessionId) return;
            Notices.Add(update.Notice);
        });
    }

    private void ClearNotices()
    {
        // Invalidate already queued UI callbacks before dispatching the clear.
        var generation = Interlocked.Increment(ref _noticeGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == Interlocked.Read(ref _noticeGeneration)) Notices.Clear();
        });
    }
}
