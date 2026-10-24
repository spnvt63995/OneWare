using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Models;
using OneWare.Settings;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpOptionsTests
{
    private static void Catalog(AcpPeer peer)
    {
        peer.ModelChoices = ["gpt-6-luna", "fixture-fast", "fixture-plain"];
        peer.EffortsByModel = new()
        {
            ["gpt-6-luna"] = ["low", "medium"],
            ["fixture-fast"] = ["medium", "high"],
            ["fixture-plain"] = []
        };
    }

    private static AcpOptionChoice Model(AcpChatService service, string value) =>
        service.Models.Single(x => x.Value == value);
    private static AcpOptionChoice Effort(AcpChatService service, string value) =>
        service.ReasoningEfforts.Single(x => x.Value == value);
    private static AcpSelection Saved(SettingsService settings) =>
        JsonSerializer.Deserialize<AcpSelection>(settings.GetSettingValue<string>(AcpChatService.SelectionKey))!;

    [AvaloniaFact]
    public async Task PickerFiltersWithoutChangingSelectionAndSendsConfirmedModelAndEffort()
    {
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings);
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        Dispatcher.UIThread.RunJobs();
        var view = Assert.IsType<AcpChatExtensionView>(h.Service.BottomUiExtension);
        var window = new Window { Content = view, Width = 360, Height = 120 };
        window.Show();
        try
        {
            var modelButton = view.FindControl<Button>("ModelButton")!;
            modelButton.Flyout!.ShowAt(modelButton);
            await AcpHarness.Until(() => view.FindControl<ListBox>("ModelList")!.ItemCount == 3 &&
                                         !h.Service.IsProjectingOptions && h.Service.CanEditOptions);
            h.Service.ModelSearchText = "FAST";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("fixture-fast", Assert.Single(h.Service.FilteredModels).Value);
            Assert.Equal("gpt-6-luna", h.Service.ModelLabel);
            Assert.Equal("", settings.GetSettingValue<string>(AcpChatService.SelectionKey));
            h.Service.ModelSearchText = "";
            Dispatcher.UIThread.RunJobs();
            var modelList = view.FindControl<ListBox>("ModelList")!;
            modelList.ContainerFromIndex(0)!.Focus();
            modelList.SelectedItem = Model(h.Service, "fixture-fast");
            await AcpHarness.Until(() => h.Service.ModelLabel == "fixture-fast" && !h.Service.IsProjectingOptions);
            Assert.True(modelList.IsKeyboardFocusWithin);
            Assert.Equal(new[] { "medium", "high" }, h.Service.ReasoningEfforts.Select(x => x.Value));
            modelButton.Flyout.Hide();
            var reasoningButton = view.FindControl<Button>("ReasoningButton")!;
            reasoningButton.Flyout!.ShowAt(reasoningButton);
            await AcpHarness.Until(() => view.FindControl<ListBox>("ReasoningList")!.ItemCount == 2 &&
                                         !h.Service.IsProjectingOptions && h.Service.CanEditReasoning);
            view.FindControl<ListBox>("ReasoningList")!.SelectedItem = Effort(h.Service, "high");
            try
            {
                await AcpHarness.Until(() => h.Service.ReasoningLabel == "high" && !h.Service.IsProjectingOptions);
            }
            catch (OperationCanceledException)
            {
                var selected = view.FindControl<ListBox>("ReasoningList")!.SelectedItem;
                var requests = string.Join(" | ", h.Peer.Requests
                    .Where(x => x.Method == "session/set_config_option").Select(x => x.Parameters));
                Assert.Fail($"effort={h.Service.ReasoningLabel}; projecting={h.Service.IsProjectingOptions}; " +
                            $"status={h.Service.OptionsStatus}; selected={selected}; requests={requests}");
            }
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            view.FindControl<ListBox>("ReasoningList")!.RaiseEvent(enter);
            Assert.True(enter.Handled);
            Assert.Empty(h.Peer.Prompts);
            Assert.Equal(new AcpSelection("fixture-fast", "high"), Saved(settings));

            var send = h.Service.SendAsync("selection check");
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
            Assert.Contains("fixture-fast", h.Service.SelectionText);
            Assert.Contains("high", h.Service.SelectionText);
            Assert.Contains(h.Peer.Requests, x => x.Method == "session/set_config_option" &&
                x.Parameters.GetProperty("configId").GetString() == "reasoning_effort" &&
                x.Parameters.GetProperty("value").GetString() == "high");
            h.Peer.Complete();
            await send;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HostSaveRestoresDefaultsAndConversationKeepsItsOwnSelection()
    {
        using var disk = new AcpTestSupport.Profile();
        var settings = AcpTestSupport.Settings();
        settings.SetSettingValue(AcpTestSupport.RetiredIntentKey, "APP_SERVER_UNCHANGED");
        await using var h = new AcpHarness(settings: settings);
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        await h.Service.SelectReasoningAsync(Effort(h.Service, "high"));
        var session = h.Service.CurrentSessionId!;
        Assert.False(File.Exists(disk.Path));
        settings.Save(disk.Path);

        var restarted = AcpTestSupport.Settings();
        restarted.Load(disk.Path);
        await using var restored = new AcpChatService(h.Host, h.Profile, () => null, () => h.Peer,
            settings: restarted);
        Assert.Equal("fixture-fast", restored.ModelLabel);
        Assert.Equal("high", restored.ReasoningLabel);
        Assert.Equal("APP_SERVER_UNCHANGED", restarted.GetSettingValue<string>(AcpTestSupport.RetiredIntentKey));

        // The application's newer default must not overwrite this conversation on resume.
        restarted.SetSettingValue(AcpChatService.SelectionKey,
            JsonSerializer.Serialize(new AcpSelection("gpt-6-luna", "low")));
        restarted.Save(disk.Path);
        restarted.Load(disk.Path);
        Assert.True(await restored.LoadSessionAsync(session));
        Assert.Equal("fixture-fast", restored.ModelLabel);
        Assert.Equal("high", restored.ReasoningLabel);
        await restored.NewChatAsync();
        Assert.Equal("gpt-6-luna", restored.ModelLabel);
        Assert.Equal("low", restored.ReasoningLabel);
    }

    [AvaloniaFact]
    public async Task RejectedSelectionKeepsSavedChoiceAndCanBeRetried()
    {
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings);
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        await h.Service.SelectReasoningAsync(Effort(h.Service, "medium"));
        var before = Saved(settings);
        h.Peer.RejectConfigId = "model";
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        Assert.Equal(before, Saved(settings));
        Assert.Equal("gpt-6-luna", h.Service.ModelLabel);
        Assert.Equal(before, h.Profile.LoadSelection(h.Service.CurrentSessionId!));
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
        h.Peer.RejectConfigId = null;
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        Assert.Equal("fixture-fast", Saved(settings).Model);
        Assert.Empty(h.Peer.Prompts);
    }

    [AvaloniaFact]
    public async Task ModelsWithoutReasoningHideThePickerAndDoNotSendAnEffort()
    {
        await using var h = new AcpHarness();
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-plain"));
        Assert.False(h.Service.ShowReasoningEffort);
        Assert.Null(h.Profile.LoadSelection(h.Service.CurrentSessionId!)!.Effort);
        h.Peer.Requests.Clear();
        var send = h.Service.SendAsync("plain model");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.DoesNotContain(h.Peer.Requests, x => x.Method == "session/set_config_option" &&
            x.Parameters.GetProperty("configId").GetString() == "reasoning_effort");
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task AgentOverridesAndStartingTurnsDoNotOverwriteUserDefaults()
    {
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings);
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        await h.Service.SelectReasoningAsync(Effort(h.Service, "medium"));
        var before = Saved(settings);
        h.Agent = new ChatAgentDefinition
        {
            Id = "override", DisplayName = "Override", Model = "fixture-fast", ReasoningEffort = "high"
        };
        await h.Service.RefreshOptionsAsync();
        Assert.False(h.Service.CanEditOptions);
        Assert.Contains("fixture-fast", h.Service.ModelLabel);
        await h.Service.SelectModelAsync(Model(h.Service, "gpt-6-luna"));
        Assert.Equal(before, Saved(settings));
        var send = h.Service.SendAsync("agent selection");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        h.Agent = null;
        Assert.False(h.Service.CanEditOptions);
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        Assert.Equal(before, Saved(settings));
        Assert.Contains("fixture-fast", h.Service.SelectionText);
        Assert.Contains("high", h.Service.SelectionText);
        h.Peer.Complete();
        await send;
        Assert.True(h.Service.CanEditOptions);
    }

    [AvaloniaFact]
    public async Task InvalidOrUnavailableSavedSelectionsAreNotSilentlyRewritten()
    {
        var settings = AcpTestSupport.Settings();
        settings.Register(AcpChatService.SelectionKey, "");
        settings.SetSettingValue(AcpChatService.SelectionKey, "broken-json");
        await using var h = new AcpHarness(settings: settings);
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        Assert.Equal("gpt-6-luna", h.Service.ModelLabel);
        Assert.Equal("broken-json", settings.GetSettingValue<string>(AcpChatService.SelectionKey));

        var missing = new AcpProfile("/unused/acp", "missing-model", "low", Path.Combine(h.Workspace, "missing"));
        await using var service = new AcpChatService(h.Host, missing, () => null, () => new AcpPeer
        {
            ModelChoices = ["gpt-6-luna"]
        });
        await service.RefreshOptionsAsync();
        Assert.Equal("missing-model", service.ModelLabel);
        Assert.Contains("unavailable", service.OptionsStatus);
        await service.SelectModelAsync(Model(service, "gpt-6-luna"));
        Assert.Equal("gpt-6-luna", service.ModelLabel);
    }
}
