using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OneWare.Codex.Services;

namespace OneWare.Codex.Views;

public partial class AcpChatExtensionView : UserControl
{
    public AcpChatExtensionView()
    {
        InitializeComponent();
        ModelButton.Flyout!.Opened += LoadOptions;
        ReasoningButton.Flyout!.Opened += LoadOptions;
        ModelList.AddHandler(KeyDownEvent, PickerKeyDown, RoutingStrategies.Tunnel);
        ReasoningList.AddHandler(KeyDownEvent, PickerKeyDown, RoutingStrategies.Tunnel);
        ContextSizeList.AddHandler(KeyDownEvent, PickerKeyDown, RoutingStrategies.Tunnel);
        ModelSearch.AddHandler(KeyDownEvent, SearchKeyDown, RoutingStrategies.Tunnel);
    }

    private async void LoadOptions(object? sender, EventArgs e)
    {
        if (DataContext is AcpChatService service) await service.RefreshOptionsAsync();
    }

    private async void ModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AcpChatService { IsProjectingOptions: false } service &&
            ModelList.SelectedItem is AcpOptionChoice choice && choice != service.SelectedModelChoice)
            await service.SelectModelAsync(choice);
    }

    private async void ReasoningSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AcpChatService { IsProjectingOptions: false } service &&
            ReasoningList.SelectedItem is AcpOptionChoice choice && choice != service.SelectedReasoningEffort)
            await service.SelectReasoningAsync(choice);
    }

    private void ModelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ModelList.SelectedItem != null) ModelButton.Flyout?.Hide();
    }

    private async void ContextSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AcpChatService { IsProjectingOptions: false } service &&
            ContextSizeList.SelectedItem is ContextSizeOption choice && choice != service.SelectedContextSize)
            await service.SelectContextSizeAsync(choice);
    }

    private void ContextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ContextSizeList.SelectedItem != null) ContextTierButton.Flyout?.Hide();
    }

    private void ReasoningPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReasoningList.SelectedItem != null) ReasoningButton.Flyout?.Hide();
    }

    private void PickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (ReferenceEquals(sender, ModelList) ? ModelButton : ReferenceEquals(sender, ContextSizeList)
            ? ContextTierButton : ReasoningButton).Flyout?.Hide();
        e.Handled = true;
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter belongs to this picker, never to the underlying chat composer.
        if (e.Key == Key.Enter) e.Handled = true;
        if (e.Key != Key.Down) return;
        if (ModelList.SelectedIndex < 0 && ModelList.ItemCount > 0) ModelList.SelectedIndex = 0;
        // ListBox itself is not focusable; keyboard events must reach the selected row.
        if (ModelList.SelectedIndex >= 0)
        {
            ModelList.ScrollIntoView(ModelList.SelectedIndex);
            ModelList.ContainerFromIndex(ModelList.SelectedIndex)?.Focus();
        }
        e.Handled = true;
    }
}
