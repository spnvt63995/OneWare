using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OneWare.Codex.Services;
using OneWare.Codex.Views;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpFeatureTests
{
    private static ChatAgentDefinition Agent(bool plan = false, string effort = "low") =>
        new()
        {
            Id = plan ? "plan" : "agent", DisplayName = plan ? "Plan" : "Agent",
            TurnMode = plan ? ChatAgentTurnMode.Plan : ChatAgentTurnMode.Interactive, ReasoningEffort = effort
        };

    private static async Task Started(AcpHarness h, int count = 1) =>
        await AcpHarness.Until(() => h.Peer.Prompts.Count == count);

    private static JsonElement[] Blocks(AcpHarness h, int index = 0) =>
        h.Peer.Prompts[index].GetProperty("prompt").EnumerateArray().ToArray();

    private static string File(AcpHarness h, string name, string text)
    {
        var path = Path.Combine(h.Workspace, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [AvaloniaFact]
    public async Task PlanUsesServerCollaborationModeAndBlocksWritesEvenWithoutAReadOnlyAgentFlag()
    {
        await using var h = new AcpHarness();
        h.Agent = Agent(plan: true);
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "acp_fixture", Description = "Must be blocked in Plan.", IsReadOnly = false,
            Handler = (Func<string, string>)(_ => throw new Exception("must not execute"))
        });
        var send = h.Service.SendAsync("plan only");
        await Started(h);
        Assert.Contains(h.Peer.Requests, x => x.Method == "session/set_config_option" &&
                                              x.Parameters.GetProperty("configId").GetString() ==
                                              "collaboration_mode" &&
                                              x.Parameters.GetProperty("value").GetString() == "plan");
        h.Peer.Tool("write");
        Assert.True((await h.Peer.Call("write")).GetProperty("isError").GetBoolean());
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await send;
        h.Agent = Agent();
        var next = h.Service.SendAsync("implement");
        await Started(h, 2);
        Assert.Equal("default", h.Peer.Requests.Last(x => x.Method == "session/set_config_option" &&
                                                          x.Parameters.GetProperty("configId").GetString() ==
                                                          "collaboration_mode").Parameters.GetProperty("value")
            .GetString());
        h.Peer.Complete();
        await next;
    }

    [AvaloniaFact]
    public async Task MissingPlanCapabilityDoesNotSendOrDiscardAttachments()
    {
        await using var h = new AcpHarness();
        h.Peer.MissingOption = "collaboration_mode";
        h.Agent = Agent(plan: true);
        h.Service.Attachments.AddFiles([File(h, "plan.txt", "KEEP")]);
        await h.Service.SendAsync("plan");
        Assert.Empty(h.Peer.Prompts);
        Assert.Single(h.Service.Attachments.Items);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task QueueFreezesContentAndAgentRunsFifoAndEmitsOneFinalIdle()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("first");
        await Started(h);
        var path = File(h, "queued 한글.txt", "ORIGINAL");
        h.Agent = Agent(plan: true, effort: "medium");
        h.Service.Attachments.AddFiles([path]);
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "queued.png");
        await h.Service.SendAsync("second", ChatSendMode.Queue);
        Assert.Empty(h.Service.Attachments.Items);
        h.Agent = Agent();
        System.IO.File.WriteAllText(path, "CHANGED");
        await h.Service.SendAsync("third", ChatSendMode.Queue);
        h.Service.Attachments.AddFiles([File(h, "next-draft.txt", "DRAFT")]);
        h.Peer.Complete();
        await Started(h, 2);
        Assert.Equal("ORIGINAL", Blocks(h, 1)[1].GetProperty("resource").GetProperty("text").GetString());
        Assert.Equal("image", Blocks(h, 1)[2].GetProperty("type").GetString());
        Assert.Contains("Plan", h.Service.SelectionText);
        Assert.Contains("medium", h.Service.SelectionText);
        Assert.Empty(h.Events.OfType<ChatIdleEvent>());
        h.Peer.Complete();
        await Started(h, 3);
        Assert.Contains("third", Blocks(h, 2)[0].GetProperty("text").GetString());
        Assert.DoesNotContain("Plan", h.Service.SelectionText);
        h.Peer.Complete();
        await send;
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
        Assert.Equal(new[] { "first", "second", "third" },
            h.Events.OfType<ChatUserMessageEvent>().Select(x => x.Content));
        Assert.Equal("next-draft.txt", Assert.Single(h.Service.Attachments.Items).DisplayName);
    }

    [AvaloniaFact]
    public async Task RemovingLastDuplicateAndClearingQueueNeverRemovesAnEarlierMessage()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("first");
        await Started(h);
        await h.Service.SendAsync("same", ChatSendMode.Queue);
        await h.Service.SendAsync("same", ChatSendMode.Queue);
        Assert.True(await h.Service.RemoveMostRecentQueuedMessageAsync());
        h.Peer.Complete();
        await Started(h, 2);
        await h.Service.SendAsync("discard", ChatSendMode.Queue);
        Assert.True(await h.Service.ClearQueuedMessagesAsync());
        h.Peer.Complete();
        await send;
        Assert.Equal(2, h.Peer.Prompts.Count);
        Assert.Equal(new[] { "first", "same" }, h.Events.OfType<ChatUserMessageEvent>().Select(x => x.Content));
        Assert.False(await h.Service.RemoveMostRecentQueuedMessageAsync());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortOrDisconnectDoesNotLaunchWaitingPrompts(bool disconnect)
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("active");
        await Started(h);
        await h.Service.SendAsync("must not run", ChatSendMode.Queue);
        if (disconnect) h.Peer.Drop();
        else await h.Service.AbortAsync();
        await send;
        Assert.Single(h.Peer.Prompts);
        Assert.Single(h.Events.OfType<ChatIdleEvent>());
        Assert.False(await h.Service.RemoveMostRecentQueuedMessageAsync());
    }

    [AvaloniaFact]
    public async Task SteerInjectsImagesAndKeepsTheOriginalPromptOwner()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("active");
        await Started(h);
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "steer.png");
        await h.Service.SendAsync("change direction", ChatSendMode.Steer);
        var request = Assert.Single(h.Peer.Requests, x => x.Method == "_session/steering").Parameters;
        Assert.Equal(h.Peer.Id, request.GetProperty("sessionId").GetString());
        Assert.Equal("image", request.GetProperty("prompt")[1].GetProperty("type").GetString());
        Assert.Single(h.Peer.Prompts);
        Assert.Empty(h.Service.Attachments.Items);
        Assert.Empty(h.Events.OfType<ChatIdleEvent>());
        h.Peer.Tool("steered");
        Assert.False((await h.Peer.Call("steered")).GetProperty("isError").GetBoolean());
        Assert.Equal(h.Peer.Id, Assert.Single(h.Starts).SessionId);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData("unsupported")]
    [InlineData("agent")]
    [InlineData("idle")]
    public async Task RejectedSteeringKeepsDraftAttachmentsAndDoesNotIssueAnRpc(string reason)
    {
        await using var h = new AcpHarness();
        h.Peer.SupportsSteering = reason != "unsupported";
        var send = reason == "idle" ? Task.CompletedTask : h.Service.SendAsync("active");
        if (reason != "idle") await Started(h);
        if (reason == "agent") h.Agent = Agent(plan: true);
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "keep.png");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("steer", ChatSendMode.Steer));
        Assert.DoesNotContain(h.Peer.Requests, x => x.Method == "_session/steering");
        Assert.Single(h.Service.Attachments.Items);
        if (reason != "idle") h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData("startedNewTurn")]
    [InlineData("failed")]
    public async Task SteeringFallbackCannotLeaveAnUnownedTurnOrRunTheQueue(string outcome)
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("active");
        await Started(h);
        await h.Service.SendAsync("waiting", ChatSendMode.Queue);
        h.Peer.SteerOutcome = outcome;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync("steer", ChatSendMode.Steer));
        await send;
        Assert.Contains("session/cancel", h.Peer.Notifications);
        Assert.False(h.Peer.IsConnected);
        Assert.Single(h.Peer.Prompts);
    }

    [AvaloniaFact]
    public async Task SteeringAcknowledgementPrecedesIdleWhenTheOriginalResponseArrivesFirst()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("active");
        await Started(h);
        h.Peer.BlockSteer = true;
        var steer = h.Service.SendAsync("steer", ChatSendMode.Steer);
        await h.Peer.SteeringReceived.Task;
        h.Peer.Complete();
        await Task.Delay(30);
        Assert.Empty(h.Events.OfType<ChatIdleEvent>());
        h.Peer.ReleaseSteer.SetResult(true);
        await steer;
        await send;
        Assert.True(h.Events.FindIndex(x => x is ChatUserMessageEvent { Content: "steer" }) <
                    h.Events.FindIndex(x => x is ChatIdleEvent));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanQuestionsUseOriginalInputEventsAndExpireOnAbort(bool abort)
    {
        await using var h = new AcpHarness();
        h.Agent = Agent(plan: true);
        var send = h.Service.SendAsync("plan");
        await Started(h);
        var response = h.Peer.ServerRequest!("elicitation/create", AcpPeer.Json(new
        {
            sessionId = h.Peer.Id, mode = "form", message = "Choose a colour",
            requestedSchema = new
            {
                type = "object", required = new[] { "colour" },
                properties = new
                {
                    colour = new
                    {
                        type = "string", oneOf = new[]
                        {
                            new { @const = "red", title = "Red" }, new { @const = "blue", title = "Blue" }
                        }
                    }
                }
            }
        }), CancellationToken.None);
        await AcpHarness.Until(() => h.Events.OfType<ChatUserInputRequestEvent>().Any());
        var question = Assert.Single(h.Events.OfType<ChatUserInputRequestEvent>());
        if (abort)
        {
            await h.Service.AbortAsync();
            question.SubmitCommand.Execute("Red");
            Assert.Equal("cancel", AcpPeer.Json(await response).GetProperty("action").GetString());
        }
        else
        {
            question.SubmitCommand.Execute("Blue");
            Assert.Equal("blue", AcpPeer.Json(await response).GetProperty("content").GetProperty("colour").GetString());
            h.Peer.Complete();
        }

        await send;
        Assert.False(question.SubmitCommand.CanExecute("Red"));
    }

    [AvaloniaFact]
    public async Task FileAttachmentUsesUnsavedBufferAndSelectionUsesOnlySelectedLiveText()
    {
        await using var h = new AcpHarness();
        var path = File(h, "buffer.txt", "DISK");
        var editor = new AcpTestSupport.EditorContext("UNSAVED selected end", path);
        h.Document = editor.Document;
        h.Documents[path] = editor.Document;
        h.Service.Attachments.AddFiles([path]);
        var send = h.Service.SendAsync("file");
        await Started(h);
        Assert.Equal("UNSAVED selected end", Blocks(h)[1].GetProperty("resource").GetProperty("text").GetString());
        h.Peer.Complete();
        await send;
        editor.Select(8, 8, reverse: true);
        var next = h.Service.SendAsync("selection");
        await Started(h, 2);
        Assert.Equal("selected", Blocks(h, 1)[1].GetProperty("resource").GetProperty("text").GetString());
        Assert.Equal("DISK", System.IO.File.ReadAllText(path));
        h.Peer.Complete();
        await next;
    }

    [AvaloniaFact]
    public async Task ImageFileAndClipboardAreCapturedAsIndependentAcpBlocksAndRemovedChipsAreExcluded()
    {
        await using var h = new AcpHarness();
        var path = Path.Combine(h.Workspace, "image.png");
        System.IO.File.WriteAllBytes(path, AcpTestSupport.Png);
        h.Service.Attachments.AddFiles([path, path]);
        var bytes = AcpTestSupport.Png.ToArray();
        Assert.True(h.Service.TryAddImageAttachment(bytes, "image/png", "paste.png"));
        bytes[0] = 0;
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "removed.png");
        h.Service.Attachments.Items.Last().RemoveCommand.Execute(null);
        var send = h.Service.SendAsync("images");
        await Started(h);
        var blocks = Blocks(h);
        Assert.Equal(3, blocks.Length);
        Assert.All(blocks.Skip(1),
            b => Assert.Equal(Convert.ToBase64String(AcpTestSupport.Png), b.GetProperty("data").GetString()));
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task PromptFailureRestoresItsFileAndImageAttachments()
    {
        await using var h = new AcpHarness();
        h.Peer.FailPrompt = true;
        h.Service.Attachments.AddFiles([File(h, "retry.txt", "RETRY")]);
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "retry.png");
        await h.Service.SendAsync("failed");
        Assert.Equal(2, h.Service.Attachments.Items.Count);
        Assert.NotNull(h.Service.Attachments.Items.Single(x => x.IsImage).ThumbnailBitmap);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaFact]
    public async Task QueueDuringInitializationIsAcceptedAndAbortClearsIt()
    {
        await using var h = new AcpHarness();
        h.Peer.BlockInitialization = true;
        var send = h.Service.SendAsync("initializing");
        await h.Peer.Initializing.Task;
        await h.Service.SendAsync("queued before handshake", ChatSendMode.Queue);
        Assert.True(await h.Service.RemoveMostRecentQueuedMessageAsync());
        await h.Service.SendAsync("cancelled queue", ChatSendMode.Queue);
        await h.Service.AbortAsync();
        await send;
        Assert.Empty(h.Peer.Prompts);
        Assert.False(await h.Service.RemoveMostRecentQueuedMessageAsync());
    }

    [AvaloniaFact]
    public async Task UnsupportedImageCapabilityKeepsTheAttachmentAndDoesNotSend()
    {
        await using var h = new AcpHarness();
        h.Peer.SupportsImages = false;
        h.Service.TryAddImageAttachment(AcpTestSupport.Png, "image/png", "keep.png");
        await h.Service.SendAsync("image");
        Assert.Empty(h.Peer.Prompts);
        Assert.Single(h.Service.Attachments.Items);
        Assert.Single(h.Events.OfType<ChatErrorEvent>());
    }

    [AvaloniaTheory]
    [InlineData(300)]
    [InlineData(440)]
    public async Task AttachmentViewBindsAndRemovesItsOwnChipAtNarrowWidths(int width)
    {
        await using var h = new AcpHarness();
        h.Service.Attachments.AddFiles([File(h, "attachment.txt", "text")]);
        var view = Assert.IsType<AcpAttachmentsView>(h.Service.TopUiExtension);
        var window = new Window { Width = width, Height = 250, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var chip = Assert.Single(h.Service.Attachments.Items);
            var remove = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                b => ReferenceEquals(b.Command, chip.RemoveCommand));
            remove.Command!.Execute(null);
            Assert.Empty(h.Service.Attachments.Items);
            Assert.True(view.Bounds.Width <= width);
        }
        finally
        {
            window.Close();
        }
    }
}