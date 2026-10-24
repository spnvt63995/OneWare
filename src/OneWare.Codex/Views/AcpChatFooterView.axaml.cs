using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OneWare.Codex.Views;

public partial class AcpChatFooterView : UserControl
{
    public AcpChatFooterView()
    {
        InitializeComponent();
        ModeList.AddHandler(KeyDownEvent, ModeKeyDown, RoutingStrategies.Tunnel);
    }

    private void ModePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ModeList.SelectedItem != null) ModeButton.Flyout?.Hide();
    }

    private void ModeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ModeButton.Flyout?.Hide();
        e.Handled = true;
    }
}
