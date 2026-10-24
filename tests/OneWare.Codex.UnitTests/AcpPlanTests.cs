using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpPlanTests
{
    private static AcpHarness Harness()
    {
        AcpHarness? h = null;
        var agents = InterfaceStub.Create<IChatAgentService>((name, args) =>
        {
            if (name == "get_SelectedAgent") return h!.Agent;
            if (name != "SelectAgent" || (string?)args![0] != BuiltInChatAgents.Agent) return null;
            h!.Agent = new ChatAgentDefinition { Id = BuiltInChatAgents.Agent, DisplayName = "Agent" };
            return true;
        });
        h = new AcpHarness(agents: agents);
        h.Agent = new ChatAgentDefinition
        {
            Id = BuiltInChatAgents.Plan, DisplayName = "Plan", TurnMode = ChatAgentTurnMode.Plan,
            IsReadOnly = true, Model = "gpt-6-luna", ReasoningEffort = "low", Instructions = "Plan only; do not implement."
        };
        h.Provider.RegisterFunction(new OneWareAiFunction
        {
            Name = "acp_fixture", Description = "Isolated writable fixture.", IsReadOnly = false,
            Handler = (Func<string, string>)(value => "IMPLEMENTED_" + value),
            ConfirmationCheck = _ => "Allow the implementation fixture?"
        });
        return h;
    }

    private static JsonElement Request(AcpHarness h, string id = "plan-review:one", string kind = "switch_mode",
        string? session = null) => AcpPeer.Json(new
    {
        sessionId = session ?? h.Peer.Id,
        toolCall = new { toolCallId = id, kind, status = "pending", rawInput = new { plan = "1. Change the fixture.\n2. Check the result." } },
        options = new[]
        {
            new { optionId = "implement_plan", kind = "allow_once", name = "Yes, implement this plan" },
            new { optionId = "revise_plan", kind = "reject_once", name = "No, and tell Codex what to do differently" }
        }
    });

    private static Task<object> Review(AcpHarness h, JsonElement? request = null, CancellationToken token = default) =>
        h.Peer.ServerRequest!("session/request_permission", request ?? Request(h), token);

    private static JsonElement TransportRequest(AcpHarness h, string id) => AcpPeer.Json(new
    {
        sessionId = h.Peer.Id, _meta = new { is_mcp_tool_approval = true },
        toolCall = new { toolCallId = id, kind = "execute", status = "pending" },
        options = new[] { new { optionId = "allow_once", kind = "allow_once" } }
    });

    private static string? Outcome(object response, string field = "outcome") =>
        AcpPeer.Json(response).GetProperty("outcome").GetProperty(field).GetString();

    [AvaloniaFact]
    public async Task StartUsesOriginalPlanCardAndLiftsOnlyPlanRestrictionWithinSamePrompt()
    {
        await using var h = Harness();
        var send = h.Service.SendAsync("Plan the fixture change.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.Contains("<proposed_plan>", h.Peer.Prompts[0].GetProperty("prompt")[0].GetProperty("text").GetString());
        h.Peer.Tool("before");
        Assert.Equal("cancelled", Outcome(await Review(h, TransportRequest(h, "before"))));
        Assert.True((await h.Peer.Call("before")).GetProperty("isError").GetBoolean());
        h.Peer.Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Planning response" } });
        var review = Review(h);
        await AcpHarness.Until(() => h.Events.OfType<ChatPlanReadyEvent>().Any());
        var offer = Assert.Single(h.Events.OfType<ChatPlanReadyEvent>());
        var card = new ChatMessagePlanReadyViewModel(offer);
        Assert.Equal("1. Change the fixture.\n2. Check the result.", card.PlanMarkdown);
        Assert.False(send.IsCompleted);
        Assert.Equal("cancelled", Outcome(await Review(h))); // duplicate callback cannot create another decision
        card.StartImplementation();
        Assert.Equal("implement_plan", Outcome(await review, "optionId"));
        Assert.Equal(BuiltInChatAgents.Agent, h.Agent!.Id);
        Assert.Equal("Implementation started", card.AnswerText);
        Assert.False(offer.StartImplementationCommand.CanExecute(null));
        await h.Service.SendAsync("Keep the implementation small.", OneWare.Essentials.Enums.ChatSendMode.Steer);
        Assert.Contains(h.Peer.Requests, x => x.Method == "_session/steering");
        h.Peer.Tool("implementation");
        Assert.Equal("allow_once", Outcome(await Review(h, TransportRequest(h, "implementation")), "optionId"));
        Assert.Empty(h.Starts);
        Assert.Empty(h.Events.OfType<ChatPermissionRequestEvent>());
        var call = h.Peer.Call("implementation");
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Empty(h.Starts);
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        Assert.False((await call).GetProperty("isError").GetBoolean());
        h.Peer.Complete();
        await send;
        var messages = h.Events.OfType<ChatMessageEvent>().ToArray();
        Assert.Equal(new[] { "Planning response", "answer" }, messages.Select(x => x.Content));
        Assert.NotEqual(messages[0].MessageId, messages[1].MessageId);
        Assert.Single(h.Peer.Prompts);
        Assert.Single(h.Starts);
        Assert.Equal(h.Peer.Id, h.Service.CurrentSessionId);
        Assert.Equal(AcpChatService.ApprovalModeDefault, h.Service.SelectedApprovalMode);
        Assert.Equal("Codex ACP · gpt-6-luna · low", h.Service.SelectionText);
    }

    [AvaloniaFact]
    public async Task UpdateKeepsPlanReadOnlyAndAllowsARevisedPlanDecision()
    {
        await using var h = Harness();
        // Even Autopilot does not accept a plan on behalf of the user.
        h.Service.SelectedApprovalMode = AcpChatService.ApprovalModeAutopilot;
        var send = h.Service.SendAsync("Plan.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        var review = Review(h);
        await AcpHarness.Until(() => h.Events.OfType<ChatPlanReadyEvent>().Any());
        var card = new ChatMessagePlanReadyViewModel(Assert.Single(h.Events.OfType<ChatPlanReadyEvent>()));
        Assert.False(review.IsCompleted);
        card.UpdatePlan();
        Assert.Equal("revise_plan", Outcome(await review, "optionId"));
        Assert.Equal(BuiltInChatAgents.Plan, h.Agent!.Id);
        Assert.Equal("Still planning — describe what to change", card.AnswerText);
        Assert.True((await h.Peer.CallTool("still-plan", "acp_fixture", new { value = "blocked" })).GetProperty("isError").GetBoolean());
        h.Peer.Complete();
        await send;
        var revised = h.Service.SendAsync("Revise the second step.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 2);
        var next = Review(h, Request(h, "plan-review:revised"));
        await AcpHarness.Until(() => h.Events.OfType<ChatPlanReadyEvent>().Count() == 2);
        h.Events.OfType<ChatPlanReadyEvent>().Last().UpdatePlanCommand.Execute(null);
        Assert.Equal("revise_plan", Outcome(await next, "optionId"));
        h.Peer.Complete();
        await revised;
        Assert.Empty(h.Starts);
        Assert.Contains("Plan", h.Service.SelectionText);
    }

    [AvaloniaTheory]
    [InlineData("abort")]
    [InlineData("new-chat")]
    [InlineData("request-cancel")]
    public async Task RetiredDecisionExpiresAndCannotSwitchAgent(string action)
    {
        await using var h = Harness();
        using var cancellation = new CancellationTokenSource();
        var send = h.Service.SendAsync("Plan.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        var review = Review(h, token: cancellation.Token);
        await AcpHarness.Until(() => h.Events.OfType<ChatPlanReadyEvent>().Any());
        var offer = Assert.Single(h.Events.OfType<ChatPlanReadyEvent>());
        var card = new ChatMessagePlanReadyViewModel(offer);
        if (action == "request-cancel") cancellation.Cancel();
        else if (action == "new-chat") await h.Service.NewChatAsync();
        else await h.Service.AbortAsync();
        Assert.Equal("cancelled", Outcome(await review));
        Dispatcher.UIThread.RunJobs();
        Assert.True(offer.IsExpired);
        Assert.True(card.IsAnswered);
        offer.StartImplementationCommand.Execute(null);
        card.StartImplementation();
        Assert.Equal(BuiltInChatAgents.Plan, h.Agent!.Id);
        Assert.False(offer.StartImplementationCommand.CanExecute(null));
        Assert.Empty(h.Starts);
        if (action == "request-cancel") h.Peer.Complete();
        await send;
    }

    [AvaloniaFact]
    public async Task UnrelatedNativePermissionAndForeignSessionNeverOfferPlanOrGrantAccess()
    {
        await using var h = Harness();
        var send = h.Service.SendAsync("Plan.");
        await AcpHarness.Until(() => h.Peer.Prompts.Count == 1);
        Assert.Equal("cancelled", Outcome(await Review(h, Request(h, kind: "execute"))));
        Assert.Equal("cancelled", Outcome(await Review(h, Request(h, id: "shell-command"))));
        Assert.Equal("cancelled", Outcome(await Review(h, Request(h, session: "another-conversation"))));
        Assert.Equal("cancelled", Outcome(await Review(h, TransportRequest(h, "unobserved-call"))));
        Assert.Empty(h.Events.OfType<ChatPlanReadyEvent>());
        Assert.Empty(h.Starts);
        Assert.Equal(BuiltInChatAgents.Plan, h.Agent!.Id);
        h.Peer.Complete();
        await send;
    }
}
