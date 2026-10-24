using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpChildUsageTests
{
    [AvaloniaFact]
    public async Task ForegroundUsageUpdatesOriginalParentFooterWithoutReplacingParentContext()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var view = Assert.IsType<AcpChatFooterView>(h.Service.FooterUiExtension);
        var window = new Window { Content = view, Width = 320, Height = 100 };
        window.Show();
        try
        {
            var send = h.Service.SendAsync("Delegate the fixture.");
            await Ready(peers, 1);
            peers[0].Update(new { sessionUpdate = "usage_update", used = 250, size = 1000 });
            var delegated = Delegate(peers[0], "worker", background: false);
            await Ready(peers, 2);
            peers[1].Update(new { sessionUpdate = "usage_update", used = 800, size = 2000 });
            peers[1].Complete(new { inputTokens = 100, outputTokens = 20, thoughtTokens = 5 });
            Assert.False(Error(await delegated));
            AssertTotals(h.Service, 1, 100, 20);
            Assert.Equal(5, h.Service.LastReasoningTokens);
            Assert.Equal(250, h.Service.ContextCurrentTokens);
            Assert.Equal(1000, h.Service.ContextTokenLimit);
            Dispatcher.UIThread.RunJobs();
            var usage = view.FindControl<Button>("UsageButton")!;
            Assert.True(usage.IsVisible);
            usage.Flyout!.ShowAt(usage);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("100 tokens", view.FindControl<TextBlock>("InputTokens")!.Text);

            peers[0].Complete(new { inputTokens = 1000, outputTokens = 50 });
            await send;
            AssertTotals(h.Service, 2, 1100, 70);
            Assert.Equal(1000, h.Service.LastInputTokens);
            Assert.False(h.Service.HasReasoningUsage);
            Dispatcher.UIThread.RunJobs();
            var content = Assert.IsAssignableFrom<Control>(((Flyout)usage.Flyout).Content);
            var texts = content.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
            Assert.Contains("1,100 tokens", texts);
            Assert.Contains("70 tokens", texts);
            Assert.Contains("1,000", view.FindControl<TextBlock>("ContextTokens")!.Text);
            usage.Flyout.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundUsageArrivesAfterParentEndsOrStopsAndStaysInItsConversation(bool stopParent)
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var send = h.Service.SendAsync("Delegate in the background.");
        await Ready(peers, 1);
        Assert.False(Error(await Delegate(peers[0], "worker", background: true)));
        await Ready(peers, 2);
        if (stopParent) await h.Service.AbortAsync();
        else peers[0].Complete(new { inputTokens = 100, outputTokens = 10 });
        await send;

        await using var other = (AcpChatService)h.Service.CreateSession();
        var independent = other.SendAsync("Another conversation.");
        await Ready(peers, 3);
        peers[2].Complete(new { inputTokens = 7, outputTokens = 1 });
        await independent;
        peers[1].Complete(new { inputTokens = 5, outputTokens = 2 });
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Any());
        AssertTotals(h.Service, stopParent ? 1 : 2, stopParent ? 5 : 105, stopParent ? 2 : 12);
        Assert.Equal(5, h.Service.LastInputTokens);
        AssertTotals(other, 1, 7, 1);
        Assert.Single(h.Events.OfType<ChatIdleEvent>());

        // Late duplicate completion/context updates must not count another request.
        peers[1].Complete(new { inputTokens = 5, outputTokens = 2 });
        peers[1].Update(new { sessionUpdate = "usage_update", used = 900, size = 1000 });
        AssertTotals(h.Service, stopParent ? 1 : 2, stopParent ? 5 : 105, stopParent ? 2 : 12);
        Assert.False(h.Service.HasContextUsage);
    }

    [AvaloniaFact]
    public async Task NestedDelegatesCountEachRequestOnceInsteadOfRollingUpSubtotalsTwice()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var send = h.Service.SendAsync("Delegate nested work.");
        await Ready(peers, 1);
        var worker = Delegate(peers[0], "worker", background: false);
        await Ready(peers, 2);
        var leaf = Delegate(peers[1], "leaf", background: false);
        await Ready(peers, 3);
        peers[2].Complete(new { inputTokens = 30, outputTokens = 3 });
        Assert.False(Error(await leaf));
        AssertTotals(h.Service, 1, 30, 3);
        peers[1].Complete(new { inputTokens = 20, outputTokens = 2 });
        Assert.False(Error(await worker));
        AssertTotals(h.Service, 2, 50, 5);
        peers[0].Complete(new { inputTokens = 10, outputTokens = 1 });
        await send;
        AssertTotals(h.Service, 3, 60, 6);
        Assert.Equal(new long?[] { 22, 33 }, h.Events.OfType<ChatSubAgentCompletedEvent>()
            .Select(x => x.TotalTokens).OrderBy(x => x));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrRetiredChildCannotAddLateUsage(bool newConversation)
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var send = h.Service.SendAsync("Start work to retire.");
        await Ready(peers, 1);
        var response = await Delegate(peers[0], "worker", background: true);
        var id = JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!)
            .RootElement.GetProperty("id").GetString();
        await Ready(peers, 2);
        if (newConversation) await h.Service.NewChatAsync();
        else
        {
            Assert.False(Error(await peers[0].CallTool("cancel", AcpPluginCatalog.AgentResultTool,
                new { id, action = "cancel" })));
            peers[0].Complete();
        }
        await send;
        peers[1].Complete(new { inputTokens = 900, outputTokens = 90 });
        peers[1].Update(new { sessionUpdate = "usage_update", used = 900, size = 1000 });
        AssertTotals(h.Service, 0, 0, 0);
        Assert.False(h.Service.HasUsageData);
    }

    [AvaloniaFact]
    public async Task MissingInvalidAndOverflowingChildUsageCannotCorruptParentTotals()
    {
        var peers = new List<AcpPeer>();
        await using var h = Harness(peers);
        var send = h.Service.SendAsync("Start background fixtures.");
        await Ready(peers, 1);
        for (var i = 0; i < 3; i++)
        {
            Assert.False(Error(await Delegate(peers[0], "worker", background: true)));
            await Ready(peers, i + 2);
        }
        peers[0].Complete(new { inputTokens = long.MaxValue - 2, outputTokens = 10 });
        await send;
        peers[1].Complete();
        peers[2].Complete(new { inputTokens = -1, outputTokens = 2 });
        peers[3].Complete(new { inputTokens = 5, outputTokens = 3 });
        await AcpHarness.Until(() => h.Events.OfType<ChatSubAgentCompletedEvent>().Count() == 3);
        AssertTotals(h.Service, 1, long.MaxValue - 2, 10);
        Assert.Equal(long.MaxValue - 2, h.Service.LastInputTokens);
    }

    private static AcpHarness Harness(List<AcpPeer> peers)
    {
        var h = new AcpHarness(factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        foreach (var name in new[] { "worker", "leaf" })
            h.Provider.RegisterAgent(new OneWareAiAgent
            {
                Name = name, Description = "Isolated usage fixture.", Instructions = "Use only the fixture.",
                Model = "gpt-6-luna", ReasoningEffort = "low"
            });
        return h;
    }

    private static Task Ready(List<AcpPeer> peers, int count) =>
        AcpHarness.Until(() => peers.Count >= count && peers[count - 1].Prompts.Count == 1);

    private static Task<JsonElement> Delegate(AcpPeer peer, string agent, bool background) =>
        peer.CallTool(Guid.NewGuid().ToString("N"), AcpPluginCatalog.DelegateTool,
            new { agent, task = "Check usage.", background });

    private static bool Error(JsonElement response) => response.GetProperty("isError").GetBoolean();

    private static void AssertTotals(AcpChatService service, long requests, long input, long output)
    {
        Assert.Equal(requests, service.SessionTotalTurns);
        Assert.Equal(input, service.SessionTotalInputTokens);
        Assert.Equal(output, service.SessionTotalOutputTokens);
    }
}
