using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpLiveModelTests
{
    [AvaloniaFact]
    public async Task BusyPickersSaveForTheNextQueuedMessageWithoutChangingTheActiveResponseOrApproval()
    {
        using var disk = new AcpTestSupport.Profile();
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(confirm: true, settings: settings);
        Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        var view = Assert.IsType<AcpChatExtensionView>(h.Service.BottomUiExtension);
        var window = new Window { Content = view, Width = 380, Height = 140 }; window.Show();
        try
        {
            var send = h.Service.SendAsync("Original request.");
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
            Assert.False(h.Service.CanEditOptions); // Runtime has not yet captured the first turn's model.
            Started(h.Peer);
            var pending = h.Peer.CallTool("waiting", "acp_fixture", new { value = "original" });
            await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
            await h.Service.SendAsync("Already queued before changing the model.", ChatSendMode.Queue);
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("ModelButton")!;
            Assert.True(button.IsEffectivelyEnabled);
            button.Flyout!.ShowAt(button);
            await AcpHarness.Until(() => !h.Service.IsProjectingOptions);
            view.FindControl<ListBox>("ModelList")!.SelectedItem = Model(h.Service, "fixture-fast");
            await AcpHarness.Until(() => h.Service.ModelLabel == "fixture-fast" && !h.Service.IsProjectingOptions);
            button.Flyout.Hide();
            Assert.Equal(new[] { "medium", "high" }, h.Service.ReasoningEfforts.Select(x => x.Value));
            var reasoning = view.FindControl<Button>("ReasoningButton")!;
            Assert.True(reasoning.IsEffectivelyEnabled);
            reasoning.Flyout!.ShowAt(reasoning);
            await AcpHarness.Until(() => !h.Service.IsProjectingOptions);
            view.FindControl<ListBox>("ReasoningList")!.SelectedItem = Effort(h.Service, "high");
            await AcpHarness.Until(() => h.Service.ReasoningLabel == "high" && !h.Service.IsProjectingOptions);
            reasoning.Flyout.Hide();
            Assert.Contains("next message", h.Service.OptionsStatus);
            Assert.Equal("Codex ACP · gpt-6-luna · low", h.Service.SelectionText);
            Assert.False(pending.IsCompleted);
            Assert.Empty(h.Starts);
            var selected = new AcpSelection("fixture-fast", "high");
            Assert.Equal(selected, h.Profile.LoadSelection(h.Service.CurrentSessionId!));
            settings.Save(disk.Path);
            var restored = AcpTestSupport.Settings(); restored.Load(disk.Path);
            await using var restart = new AcpHarness(settings: restored);
            Assert.Equal("fixture-fast", restart.Service.ModelLabel);
            Assert.Equal("high", restart.Service.ReasoningLabel);
            await h.Service.SendAsync("Steering keeps the running model.", ChatSendMode.Steer);
            Assert.Single(h.Peer.Prompts);
            h.Events.OfType<ChatPermissionRequestEvent>().Single().AllowCommand.Execute(null);
            Assert.False((await pending).GetProperty("isError").GetBoolean());
            h.Peer.Complete();
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
            Assert.Equal(new[] { new AcpSelection("gpt-6-luna", "low"), selected }, h.Peer.PromptSelections);
            Assert.Contains(h.Events.OfType<ChatMessageEvent>(), e => e.Model == "gpt-6-luna");
            Assert.Equal("Codex ACP · fixture-fast · high", h.Service.SelectionText);
            Assert.DoesNotContain("next message", h.Service.OptionsStatus);
            h.Peer.Complete(); await send;
            Assert.Equal(1, h.Peer.NewSessions);
            Assert.DoesNotContain("session/cancel", h.Peer.Notifications);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RejectedAndRapidSelectionsKeepOnlyConfirmedChoicesInOrder()
    {
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings); Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        var send = h.Service.SendAsync("Running.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1); Started(h.Peer);
        h.Peer.RejectConfigId = "model";
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        Assert.Equal("gpt-6-luna", h.Service.ModelLabel);
        Assert.Equal("", settings.GetSettingValue<string>(AcpChatService.SelectionKey));
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
        h.Peer.RejectConfigId = null; h.Peer.BlockConfigId = "model";
        var first = h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        await h.Peer.ConfigStarted.Task;
        var last = h.Service.SelectModelAsync(Model(h.Service, "gpt-6-luna"));
        Assert.False(last.IsCompleted);
        h.Peer.ReleaseConfig.SetResult(true);
        await Task.WhenAll(first, last);
        Assert.Equal(new AcpSelection("gpt-6-luna", "medium"), h.Profile.LoadSelection(h.Service.CurrentSessionId!));
        Assert.Equal("", h.Service.OptionsStatus.Replace("Applies to the next message.", ""));
        Assert.Single(h.Peer.Prompts);
        h.Peer.Complete(); await send;
    }

    [AvaloniaFact]
    public async Task LateConfigReplyAfterNewChatCannotReplaceTheNewConversationsSelection()
    {
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings); Catalog(h.Peer);
        await h.Service.RefreshOptionsAsync();
        var oldSession = h.Service.CurrentSessionId!;
        var send = h.Service.SendAsync("Old conversation.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1); Started(h.Peer);
        h.Peer.BlockConfigId = "model";
        var change = h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        await h.Peer.ConfigStarted.Task;
        await h.Service.NewChatAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await send;
        h.Peer.ReleaseConfig.SetResult(true);
        await change;
        Assert.Null(h.Service.CurrentSessionId);
        Assert.Equal("gpt-6-luna", h.Service.ModelLabel);
        Assert.Equal("low", h.Service.ReasoningLabel);
        Assert.Equal("", settings.GetSettingValue<string>(AcpChatService.SelectionKey));
        Assert.NotEqual("fixture-fast", h.Profile.LoadSelection(oldSession)?.Model);
        Assert.Empty(h.Service.Models);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task AutomaticParentUsesTheNewSelectionWhileTheChildKeepsItsPinnedModel()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () =>
        {
            var peer = new AcpPeer(); Catalog(peer); peers.Add(peer); return peer;
        });
        h.Provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "worker", Description = "Model fixture", Instructions = "Reply briefly.",
            Model = "gpt-6-luna", ReasoningEffort = "low"
        });
        var send = h.Service.SendAsync("Delegate.");
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        Started(peers[0]);
        Assert.False((await peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool,
            new { agent = "worker", task = "Reply.", background = true })).GetProperty("isError").GetBoolean());
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        await h.Service.SelectModelAsync(Model(h.Service, "fixture-fast"));
        await h.Service.SelectReasoningAsync(Effort(h.Service, "high"));
        peers[0].Complete(); await send;
        peers[1].Complete();
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        Assert.Equal(new AcpSelection("fixture-fast", "high"), peers[0].PromptSelections[1]);
        Assert.Equal(new AcpSelection("gpt-6-luna", "low"), Assert.Single(peers[1].PromptSelections));
        peers[0].Complete();
        await AcpHarness.Until(() => h.Events.OfType<ChatIdleEvent>().Count() == 2);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
    }

    private static void Catalog(AcpPeer peer)
    {
        peer.ModelChoices = ["gpt-6-luna", "fixture-fast"];
        peer.EffortsByModel = new() { ["gpt-6-luna"] = ["low", "medium"], ["fixture-fast"] = ["medium", "high"] };
    }
    private static void Started(AcpPeer peer) => peer.Update(new
        { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "ORIGINAL:" } });
    private static AcpOptionChoice Model(AcpChatService service, string value) => service.Models.Single(x => x.Value == value);
    private static AcpOptionChoice Effort(AcpChatService service, string value) => service.ReasoningEfforts.Single(x => x.Value == value);
}
