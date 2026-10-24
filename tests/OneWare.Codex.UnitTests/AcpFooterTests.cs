using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpFooterTests
{
    [AvaloniaFact]
    public async Task FooterPickerUsesHostPersistenceWithoutChangingOtherProviderSettings()
    {
        using var disk = new AcpTestSupport.Profile();
        var settings = AcpTestSupport.Settings();
        settings.SetSettingValue(AcpTestSupport.RetiredIntentKey, "APP_SERVER_UNCHANGED");
        await using var h = new AcpHarness(settings: settings);
        var view = Assert.IsType<AcpChatFooterView>(h.Service.FooterUiExtension);
        var window = new Window { Content = view, Width = 320, Height = 100 };
        window.Show();
        try
        {
            Assert.Equal(AcpChatService.ApprovalModeDefault, h.Service.SelectedApprovalMode);
            Assert.False(view.FindControl<Button>("UsageButton")!.IsVisible);
            var button = view.FindControl<Button>("ModeButton")!;
            button.Flyout!.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("ModeList")!;
            Assert.Equal(3, list.ItemCount);
            list.SelectedItem = AcpChatService.ApprovalModeAutopilot;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AcpChatService.ApprovalModeAutopilot, h.Service.SelectedApprovalMode);
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            list.RaiseEvent(enter);
            Assert.True(enter.Handled);
            Assert.Empty(h.Peer.Prompts);
            Assert.False(File.Exists(disk.Path));
            settings.Save(disk.Path);

            var restored = AcpTestSupport.Settings();
            restored.Load(disk.Path);
            await using var restart = new AcpHarness(settings: restored);
            Assert.Equal(AcpChatService.ApprovalModeAutopilot, restart.Service.SelectedApprovalMode);
            Assert.Equal("APP_SERVER_UNCHANGED", restored.GetSettingValue<string>(AcpTestSupport.RetiredIntentKey));
            Assert.False(restart.Service.HasUsageData);

            restored.SetSettingValue(AcpChatService.ApprovalModeKey, "future-unknown-mode");
            restored.Save(disk.Path);
            var unknown = AcpTestSupport.Settings();
            unknown.Load(disk.Path);
            await using var fallback = new AcpHarness(settings: unknown);
            Assert.Equal(AcpChatService.ApprovalModeDefault, fallback.Service.SelectedApprovalMode);
            Assert.Equal("future-unknown-mode", unknown.GetSettingValue<string>(AcpChatService.ApprovalModeKey));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(AcpChatService.ApprovalModeDefault, false)]
    [InlineData(AcpChatService.ApprovalModeBypass, true)]
    [InlineData(AcpChatService.ApprovalModeAutopilot, true)]
    public async Task ApprovalModesControlConfirmationsButNeverOverridePlanOrToolRestrictions(string mode, bool allows)
    {
        await using var h = new AcpHarness(confirm: true);
        h.Service.SelectedApprovalMode = mode;
        var send = h.Service.SendAsync("use the isolated fixture");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.True(h.Service.CanChangeApprovalMode);
        Assert.Equal(mode, h.Service.SelectedApprovalMode);
        h.Peer.Tool("first");
        var call = h.Peer.Call("first");
        if (!allows)
        {
            await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
            Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).DenyCommand.Execute(null);
        }
        Assert.Equal(!allows, (await call).GetProperty("isError").GetBoolean());
        Assert.Equal(allows ? 1 : 0, h.Starts.Count);
        if (allows) Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
        if (mode == AcpChatService.ApprovalModeBypass)
        {
            var question = Ask(h, new { answer = new { type = "string" } });
            await AcpHarness.Until(() => h.Events.OfType<ChatUserInputRequestEvent>().Any());
            h.Events.OfType<ChatUserInputRequestEvent>().Last().SubmitCommand.Execute("a real user answer");
            Assert.Equal("a real user answer", (await question).GetProperty("content").GetProperty("answer").GetString());
        }
        h.Peer.Complete();
        await send;
        Assert.True(h.Service.CanChangeApprovalMode);

        // Automatic approval does not make a modifying tool legal in Plan.
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "acp_fixture", Description = "Must not run in Plan", IsReadOnly = false,
            Handler = (Func<string, string>)(_ => throw new Exception("modifying tool escaped Plan"))
        });
        h.Agent = new ChatAgentDefinition { Id = "plan", DisplayName = "Plan", TurnMode = ChatAgentTurnMode.Plan };
        var plan = h.Service.SendAsync("plan only");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        h.Peer.Tool("blocked");
        Assert.True((await h.Peer.Call("blocked")).GetProperty("isError").GetBoolean());
        Assert.Equal(allows ? 1 : 0, h.Starts.Count);
        // Native ACP permissions remain outside the OneWare tool approval path.
        var native = await h.Peer.ServerRequest!("session/request_permission", AcpPeer.Json(new { }), CancellationToken.None);
        Assert.Equal("cancelled", AcpPeer.Json(native).GetProperty("outcome").GetProperty("outcome").GetString());
        h.Peer.Complete();
        await plan;
    }

    [AvaloniaFact]
    public async Task AutopilotDoesNotWaitForQuestionsOrInventConstrainedAnswers()
    {
        await using var h = new AcpHarness();
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeAutopilot;
        var send = h.Service.SendAsync("continue autonomously");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.Contains("current approval mode", h.Peer.Prompts[0].GetProperty("prompt")[0].GetProperty("text").GetString());
        var free = await Ask(h, new { answer = new { type = "string" } });
        Assert.Equal("accept", free.GetProperty("action").GetString());
        Assert.Contains("user is not available", free.GetProperty("content").GetProperty("answer").GetString());
        var constrained = await Ask(h, new { answer = new { type = "string", @enum = new[] { "red", "blue" } } });
        Assert.Equal("decline", constrained.GetProperty("action").GetString());
        var other = await Ask(h, new
        {
            answer = new { type = "string", oneOf = new[] { new { @const = "red" }, new { @const = "None of the above" } } },
            note = new { type = "string", _meta = new { codex = new { questionId = "answer", role = "user_note" } } }
        });
        Assert.Equal("None of the above", other.GetProperty("content").GetProperty("answer").GetString());
        Assert.Contains("user is not available", other.GetProperty("content").GetProperty("note").GetString());
        var secret = await Ask(h, new { answer = new { type = "string", _meta = new { codex = new { isSecret = true } } } });
        Assert.Equal("decline", secret.GetProperty("action").GetString());
        Assert.Empty(h.Events.OfType<ChatUserInputRequestEvent>());
        h.Peer.Complete();
        await send;
        Assert.Equal("cancel", (await Ask(h, new { answer = new { type = "string" } })).GetProperty("action").GetString());
    }

    [AvaloniaFact]
    public async Task UsageCountsPromptResponsesOnceAndRendersOnlyKnownOwnedContext()
    {
        await using var h = new AcpHarness();
        var view = Assert.IsType<AcpChatFooterView>(h.Service.FooterUiExtension);
        var window = new Window { Content = view, Width = 320, Height = 100 };
        window.Show();
        try
        {
            var send = h.Service.SendAsync("first");
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
            h.Peer.Update(new { sessionUpdate = "usage_update", used = 500, size = 1000 }, "foreign");
            Assert.False(h.Service.HasUsageData);
            h.Peer.Update(new { sessionUpdate = "usage_update", used = 100, size = 1000 });
            h.Peer.Update(new { sessionUpdate = "usage_update", used = 100, size = 1000 });
            h.Peer.Update(new { sessionUpdate = "usage_update", used = -1, size = 0 });
            Assert.Equal(0, h.Service.SessionTotalTurns);
            Assert.Equal(100, h.Service.ContextCurrentTokens);
            h.Peer.Complete(new { inputTokens = 300, outputTokens = 40, thoughtTokens = 8 });
            await send;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, h.Service.SessionTotalTurns);
            Assert.Equal(300, h.Service.LastInputTokens);
            Assert.Equal(8, h.Service.LastReasoningTokens);
            var usage = view.FindControl<Button>("UsageButton")!;
            Assert.True(usage.IsVisible);
            usage.Flyout!.ShowAt(usage);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("300", view.FindControl<TextBlock>("InputTokens")!.Text);
            Assert.Contains("1,000", view.FindControl<TextBlock>("ContextTokens")!.Text);
            usage.Flyout.Hide();
            h.Peer.Update(new { sessionUpdate = "usage_update", used = 900, size = 1000 });
            Assert.Equal(100, h.Service.ContextCurrentTokens);

            var next = h.Service.SendAsync("second");
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
            h.Peer.Complete(new { inputTokens = 100, outputTokens = 20 });
            await next;
            Assert.Equal(2, h.Service.SessionTotalTurns);
            Assert.Equal(400, h.Service.SessionTotalInputTokens);
            Assert.Equal(60, h.Service.SessionTotalOutputTokens);
            Assert.False(h.Service.HasReasoningUsage);
            var unknown = h.Service.SendAsync("usage unavailable");
            await AcpHarness.Until(() => h.Peer.Prompts.Count == 3);
            h.Peer.Complete();
            await unknown;
            Assert.False(h.Service.HasTurnUsage);
            Assert.Equal(2, h.Service.SessionTotalTurns);
            Assert.Equal(400, h.Service.SessionTotalInputTokens);
            await h.Service.NewChatAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(h.Service.HasUsageData);
            Assert.False(usage.IsVisible);
        }
        finally { window.Close(); }
    }

    private static async Task<JsonElement> Ask(AcpHarness h, object properties) => AcpPeer.Json(
        await h.Peer.ServerRequest!("elicitation/create", AcpPeer.Json(new
        {
            sessionId = h.Peer.Id, mode = "form", message = "Choose",
            requestedSchema = new { type = "object", required = new[] { "answer" }, properties }
        }), CancellationToken.None));
}
