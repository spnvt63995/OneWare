using System.Text.Json;
using Avalonia.Headless.XUnit;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpQuestionTests
{
    [AvaloniaFact]
    public async Task RuntimeFallbackUsesOwnedMcpAndOriginalQuestionCardWithoutHostSideEffects()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("ask a question");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.Contains("mcp__oneware__oneware_ask_user",
            h.Peer.Prompts[0].GetProperty("prompt")[0].GetProperty("text").GetString());
        var args = new { question = "Which label?", choices = new[] { "Alpha", "Beta" }, allowFreeform = true };
        var foreign = await h.Peer.CallTool("foreign-question", AcpChatService.QuestionTool, args, session: "foreign");
        Assert.True(foreign.GetProperty("isError").GetBoolean());
        Assert.Empty(h.Events.OfType<ChatUserInputRequestEvent>());
        var response = h.Peer.CallTool("owned-question", AcpChatService.QuestionTool, args);
        var card = new ChatMessageUserInputRequestViewModel(await Question(h, 1)) { FreeformText = "CUSTOM_LABEL" };
        Assert.Equal(["Alpha", "Beta"], card.Event.Choices);
        Assert.True(card.Event.AllowFreeform);
        card.SubmitFreeform();
        var reply = await response;
        Assert.False(reply.GetProperty("isError").GetBoolean());
        using var result = JsonDocument.Parse(reply.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("CUSTOM_LABEL", result.RootElement.GetProperty("answer").GetString());
        Assert.True(result.RootElement.GetProperty("wasFreeform").GetBoolean());
        var duplicate = await h.Peer.CallTool("owned-question", AcpChatService.QuestionTool, args);
        Assert.True(duplicate.GetProperty("isError").GetBoolean());
        Assert.Single(h.Events.OfType<ChatUserInputRequestEvent>());
        Assert.Empty(h.Starts);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalQuestionCardReturnsChoiceOrTypedAlternative(bool typed)
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("ask for a preference");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        var response = Ask(h, Properties(), ["colour"]);
        var question = await Question(h, 1);
        Assert.Equal("Which colour should we use?", question.Question);
        Assert.Equal(["Red", "Blue"], question.Choices);
        Assert.True(question.AllowFreeform);
        Assert.False(question.SubmitCommand.CanExecute(" "));
        var card = new ChatMessageUserInputRequestViewModel(question);
        if (typed)
        {
            card.FreeformText = "  custom teal  ";
            card.SubmitFreeform();
        }
        else card.SelectChoice("Blue");
        var reply = AcpPeer.Json(await response.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("accept", reply.GetProperty("action").GetString());
        var content = reply.GetProperty("content");
        Assert.Equal(typed ? "None of the above" : "blue", content.GetProperty("colour").GetString());
        if (typed) Assert.Equal("custom teal", content.GetProperty("colour_note").GetString());
        else Assert.False(content.TryGetProperty("colour_note", out _));
        Assert.True(card.IsAnswered);
        Assert.False(question.SubmitCommand.CanExecute("Red"));
        Assert.Single(h.Events.OfType<ChatUserInputRequestEvent>());
        Assert.Single(h.Peer.Prompts);
        Assert.Equal(h.Peer.Id, h.Service.CurrentSessionId);
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task MultipleQuestionsAssociateOptionalNotesByMetadataNotFieldOrderOrSuffix()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("ask two questions");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        var properties = new Dictionary<string, object>
        {
            ["unusual_note2"] = Note("second"),
            ["first"] = Choice(),
            ["unusual_note1"] = Note("first"),
            ["second"] = Choice()
        };
        var response = Ask(h, properties, ["first", "second"]);
        var first = new ChatMessageUserInputRequestViewModel(await Question(h, 1)) { FreeformText = "first custom" };
        first.SubmitFreeform();
        Assert.False(response.IsCompleted);
        var second = new ChatMessageUserInputRequestViewModel(await Question(h, 2)) { FreeformText = "second custom" };
        second.SubmitFreeform();
        var content = AcpPeer.Json(await response).GetProperty("content");
        Assert.Equal("first custom", content.GetProperty("unusual_note1").GetString());
        Assert.Equal("second custom", content.GetProperty("unusual_note2").GetString());
        Assert.Equal("None of the above", content.GetProperty("first").GetString());
        Assert.Equal("None of the above", content.GetProperty("second").GetString());
        Assert.Equal(4, content.EnumerateObject().Count());
        Assert.Equal(2, h.Events.OfType<ChatUserInputRequestEvent>().Count());
        h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task ClosedChoicesAndUnsafeOrUnmatchedNotesDoNotAcceptArbitraryText()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("ask a constrained question");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        var response = Ask(h, new Dictionary<string, object>
        {
            ["colour"] = Choice(), ["foreign_note"] = Note("another-question")
        }, ["colour"]);
        var question = await Question(h, 1);
        Assert.False(question.AllowFreeform);
        Assert.False(question.SubmitCommand.CanExecute("custom"));
        question.SubmitCommand.Execute("custom");
        Assert.False(response.IsCompleted);
        question.SubmitCommand.Execute("Blue");
        Assert.Equal("blue", AcpPeer.Json(await response).GetProperty("content").GetProperty("colour").GetString());
        foreach (var properties in new[]
                 {
                     new Dictionary<string, object> { ["colour"] = Choice(), ["note"] = Note("colour", secret: true) },
                     new Dictionary<string, object> { ["colour"] = Choice(), ["a"] = Note("colour"), ["b"] = Note("colour") },
                     new Dictionary<string, object> { ["colour"] = new { type = "string", _meta = new { codex = new { isSecret = true } } } }
                 })
            Assert.Equal("cancel", AcpPeer.Json(await Ask(h, properties, ["colour"])).GetProperty("action").GetString());
        Assert.Single(h.Events.OfType<ChatUserInputRequestEvent>());
        h.Peer.Complete();
        await send;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrRetiredQuestionCannotSubmitTypedAnswer(bool newChat)
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("ask a question");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        using var cancellation = new CancellationTokenSource();
        var response = Ask(h, Properties(), ["colour"], cancellation.Token);
        var question = await Question(h, 1);
        if (newChat) await h.Service.NewChatAsync();
        else cancellation.Cancel();
        Assert.Equal("cancel", AcpPeer.Json(await response.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("action").GetString());
        Assert.False(question.SubmitCommand.CanExecute("late custom"));
        question.SubmitCommand.Execute("late custom");
        if (!newChat) h.Peer.Complete();
        await send;
        Assert.Single(h.Events.OfType<ChatUserInputRequestEvent>());
    }

    private static object Choice() => new
    {
        type = "string", title = "Which colour should we use?", description = "Colour",
        _meta = new { codex = new { isOther = true, isSecret = false } },
        oneOf = new[]
        {
            new { @const = "red", title = "Red" }, new { @const = "blue", title = "Blue" },
            new { @const = "None of the above", title = "None of the above" }
        }
    };

    private static object Note(string questionId, bool secret = false) => new
    {
        type = "string", title = "Additional answer or note",
        _meta = new { codex = new { questionId, role = "user_note", isSecret = secret } }
    };

    private static Dictionary<string, object> Properties() => new()
    {
        ["colour"] = Choice(), ["colour_note"] = Note("colour")
    };

    private static Task<object> Ask(AcpHarness h, Dictionary<string, object> properties, string[] required,
        CancellationToken token = default) => h.Peer.ServerRequest!("elicitation/create", AcpPeer.Json(new
    {
        sessionId = h.Peer.Id, mode = "form", message = "Codex needs your input to continue.",
        requestedSchema = new { type = "object", properties, required }
    }), token);

    private static async Task<ChatUserInputRequestEvent> Question(AcpHarness h, int count)
    {
        await AcpHarness.Until(() => h.Events.OfType<ChatUserInputRequestEvent>().Count() == count);
        return h.Events.OfType<ChatUserInputRequestEvent>().Last();
    }
}
