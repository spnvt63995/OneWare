using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OneWare.Codex.Services;

namespace OneWare.Codex.Views;

public partial class AcpCommandsView : UserControl
{
    public AcpCommandsView()
    {
        InitializeComponent();
        CommandsButton.Flyout!.Opened += LoadCommands;
        CommandSearch.AddHandler(KeyDownEvent, SearchKeyDown, RoutingStrategies.Tunnel);
        CommandList.AddHandler(KeyDownEvent, ListKeyDown, RoutingStrategies.Tunnel);
        CommandArguments.AddHandler(KeyDownEvent, ArgumentsKeyDown, RoutingStrategies.Tunnel);
    }

    private async void LoadCommands(object? sender, EventArgs e)
    {
        if (DataContext is AcpChatService service) await service.RefreshCommandsAsync();
        if (CommandsButton.Flyout?.IsOpen == true) CommandSearch.Focus();
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter or Key.Return)) return;
        e.Handled = true;
        if (CommandList.ItemCount == 0) return;
        if (CommandList.SelectedIndex < 0) CommandList.SelectedIndex = 0;
        CommandList.ScrollIntoView(CommandList.SelectedIndex);
        CommandList.ContainerFromIndex(CommandList.SelectedIndex)?.Focus();
    }

    private void ListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        if (CommandArguments.IsVisible) CommandArguments.Focus();
        else RunCommandButton.Focus();
    }

    private void ArgumentsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        RunCommandButton.Focus();
    }

    private async void RunCommandClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AcpChatService { CanRunCommand: true } service) return;
        CommandsButton.Flyout?.Hide();
        await service.RunSelectedCommandAsync();
    }
}
