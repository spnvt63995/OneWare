using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Codex.Protocol;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpContextTests
{
    private static Task<IReadOnlyDictionary<string, AcpContextLimits>> Catalog(CancellationToken _) =>
        Task.FromResult<IReadOnlyDictionary<string, AcpContextLimits>>(new Dictionary<string, AcpContextLimits>
        {
            ["gpt-6-luna"] = new(272_000, 872_000), ["single-size"] = new(128_000, 128_000)
        });

    [Fact]
    public void CatalogUsesOnlyAdvertisedPositiveIntegerLimits()
    {
        var catalog = AcpContextCatalog.Parse("""
            {"models":[
                {"slug":"valid","context_window":272000,"max_context_window":872000},
                {"slug":"single","context_window":128000,"max_context_window":128000},
                {"slug":"missing","context_window":100000},
                {"slug":"reversed","context_window":200000,"max_context_window":100000},
                {"slug":"negative","context_window":-1,"max_context_window":100000},
                {"slug":"text","context_window":"200000","max_context_window":400000},
                {"slug":"fraction","context_window":1.5,"max_context_window":400000}
            ]}
            """);
        Assert.Equal(2, catalog.Count);
        Assert.Equal(new AcpContextLimits(272000, 872000), catalog["valid"]);
        Assert.Empty(AcpContextCatalog.Parse("[]"));
    }

    [Fact]
    public void LaunchConfigForwardsContextWithoutChangingSandboxOrOtherFeatures()
    {
        var profile = new AcpProfile("/unused/acp", "gpt-6-luna", "low", "/unused/profile");
        using var before = JsonDocument.Parse(AcpConnection.CreateConfig(profile, null));
        using var after = JsonDocument.Parse(AcpConnection.CreateConfig(profile, 872000));
        Assert.False(before.RootElement.TryGetProperty("model_context_window", out _));
        Assert.Equal(872000, after.RootElement.GetProperty("model_context_window").GetInt64());
        foreach (var field in before.RootElement.EnumerateObject())
            Assert.Equal(field.Value.GetRawText(), after.RootElement.GetProperty(field.Name).GetRawText());
    }

    [AvaloniaFact]
    public async Task PickerUsesCatalogLabelsAndHidesSingleOrUnknownModelLimits()
    {
        await using var h = new AcpHarness(contextCatalog: Catalog);
        h.Peer.ModelChoices = ["gpt-6-luna", "single-size", "unknown"];
        await h.Service.RefreshOptionsAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(h.Service.ShowContextSize);
        Assert.Equal(new[] { "272K", "872K" }, h.Service.ContextSizes.Select(x => x.Label));
        var view = Assert.IsType<AcpChatExtensionView>(h.Service.BottomUiExtension);
        Assert.True(view.FindControl<Button>("ContextTierButton")!.IsVisible);
        await h.Service.SelectModelAsync(h.Service.Models.Single(x => x.Value == "single-size"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(h.Service.ShowContextSize);
        Assert.Single(h.Service.ContextSizes);
        await h.Service.SelectModelAsync(h.Service.Models.Single(x => x.Value == "unknown"));
        Assert.False(h.Service.ShowContextSize);
        Assert.Empty(h.Service.ContextSizes);
        Assert.Empty(h.Peer.Prompts);
    }

    [AvaloniaFact]
    public async Task SelectionResumesSameConversationAndRestoresItsOwnTierAcrossModelChanges()
    {
        using var disk = new AcpTestSupport.Profile();
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings, contextCatalog: Catalog);
        h.Peer.ModelChoices = ["gpt-6-luna", "single-size"];
        await h.Service.RefreshOptionsAsync();
        var id = h.Service.CurrentSessionId!;
        await h.Service.SelectContextSizeAsync(h.Service.ContextSizes.Last());
        Assert.Equal(id, h.Service.CurrentSessionId);
        Assert.Equal(1, h.Peer.Resumes);
        Assert.Equal(AcpChatService.LongContextTier, h.Profile.LoadSelection(id)!.ContextTier);
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
        settings.Save(disk.Path);
        await h.Service.SelectModelAsync(h.Service.Models.Single(x => x.Value == "single-size"));
        Assert.Equal(AcpChatService.DefaultContextTier, h.Service.SelectedContextSize!.Tier);
        await h.Service.SelectModelAsync(h.Service.Models.Single(x => x.Value == "gpt-6-luna"));
        Assert.Equal(AcpChatService.LongContextTier, h.Service.SelectedContextSize!.Tier);

        var restoredSettings = AcpTestSupport.Settings();
        restoredSettings.Register(AcpChatService.SelectionKey, "");
        restoredSettings.Load(disk.Path);
        Assert.Equal(AcpChatService.LongContextTier,
            JsonSerializer.Deserialize<AcpSelection>(restoredSettings.GetSettingValue<string>(
                AcpChatService.SelectionKey))!.ContextTier);
        restoredSettings.SetSettingValue(AcpChatService.SelectionKey,
            JsonSerializer.Serialize(new AcpSelection("gpt-6-luna", "low")));
        await h.Service.DisposeAsync();
        await using var restored = new AcpChatService(h.Host, h.Profile, () => null, () => h.Peer,
            settings: restoredSettings, contextCatalog: Catalog);
        Assert.True(await restored.LoadSessionAsync(id));
        Assert.Equal(id, restored.CurrentSessionId);
        Assert.Equal(AcpChatService.LongContextTier, restored.SelectedContextSize!.Tier);
        await restored.SelectContextSizeAsync(restored.ContextSizes.First());
        Assert.Equal(AcpChatService.DefaultContextTier, h.Profile.LoadSelection(id)!.ContextTier);
        Assert.Empty(h.Peer.Prompts);
    }

    [AvaloniaFact]
    public async Task FailedResumeDoesNotSaveOrReplaceTheConversation()
    {
        var settings = AcpTestSupport.Settings();
        await using var h = new AcpHarness(settings: settings, contextCatalog: Catalog);
        await h.Service.RefreshOptionsAsync();
        var id = h.Service.CurrentSessionId!;
        var before = settings.GetSettingValue<string>(AcpChatService.SelectionKey);
        h.Peer.FailResume = true;
        await h.Service.SelectContextSizeAsync(h.Service.ContextSizes.Last());
        Assert.Equal(before, settings.GetSettingValue<string>(AcpChatService.SelectionKey));
        Assert.Equal(AcpChatService.DefaultContextTier, h.Profile.LoadSelection(id)!.ContextTier);
        Assert.Equal(id, h.Service.CurrentSessionId);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
        h.Peer.FailResume = false;
        await h.Service.SelectContextSizeAsync(h.Service.ContextSizes.Last());
        Assert.Equal(AcpChatService.LongContextTier, h.Profile.LoadSelection(id)!.ContextTier);
    }

    [AvaloniaFact]
    public async Task SiblingConversationsKeepTheirOwnTierWhileSharingDefaults()
    {
        var settings = AcpTestSupport.Settings();
        var peer = new AcpPeer();
        await using var h = new AcpHarness(settings: settings, factory: () => peer,
            contextCatalog: Catalog);
        await h.Service.RefreshOptionsAsync();
        await h.Service.SelectContextSizeAsync(h.Service.ContextSizes.Last());
        Assert.Empty(h.Events.OfType<ChatErrorEvent>());
        var first = h.Service.CurrentSessionId!;
        peer = new AcpPeer();
        await using var sibling = (AcpChatService)h.Service.CreateSession();
        await sibling.RefreshOptionsAsync();
        Assert.Equal(AcpChatService.LongContextTier, sibling.SelectedContextSize!.Tier);
        await sibling.SelectContextSizeAsync(sibling.ContextSizes.First());
        Assert.NotEqual(first, sibling.CurrentSessionId);
        Assert.Equal(AcpChatService.DefaultContextTier, sibling.SelectedContextSize!.Tier);
        Assert.Equal(AcpChatService.LongContextTier, h.Service.SelectedContextSize!.Tier);
        Assert.Equal(AcpChatService.LongContextTier, h.Profile.LoadSelection(first)!.ContextTier);
        Assert.Equal(AcpChatService.DefaultContextTier,
            h.Profile.LoadSelection(sibling.CurrentSessionId!)!.ContextTier);
    }

    [AvaloniaFact]
    public async Task ContextChangeDoesNotInterruptAnActiveResponseAndCatalogFailureKeepsChatUsable()
    {
        await using var h = new AcpHarness(contextCatalog: Catalog);
        await h.Service.RefreshOptionsAsync();
        var send = h.Service.SendAsync("fixture");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.False(h.Service.CanEditContextSize);
        await h.Service.SelectContextSizeAsync(h.Service.ContextSizes.Last());
        Assert.Equal(0, h.Peer.Resumes);
        Assert.Equal(AcpChatService.DefaultContextTier, h.Service.SelectedContextSize!.Tier);
        h.Peer.Complete();
        await send;
        await using var unavailable = new AcpHarness(contextCatalog: _ =>
            Task.FromException<IReadOnlyDictionary<string, AcpContextLimits>>(new IOException("unsupported")));
        await unavailable.Service.RefreshOptionsAsync();
        Assert.False(unavailable.Service.ShowContextSize);
        Assert.Empty(unavailable.Events.OfType<ChatErrorEvent>());
        Assert.NotEmpty(unavailable.Service.Models);
    }
}
