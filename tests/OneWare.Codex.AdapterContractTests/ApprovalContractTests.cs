using Xunit;
using static OneWare.Codex.AdapterContractTests.ApprovalTestSupport;

namespace OneWare.Codex.AdapterContractTests;

public sealed class ApprovalContractTests
{
    [Theory]
    [InlineData("completed", "once")]
    [InlineData("completed", "chat")]
    [InlineData("cancelled", "once")]
    [InlineData("cancelled", "chat")]
    [InlineData("new-prompt", "once")]
    [InlineData("new-prompt", "chat")]
    [InlineData("new-session", "once")]
    [InlineData("new-session", "chat")]
    [InlineData("new-connection", "once")]
    [InlineData("new-connection", "chat")]
    [Trait("Contract", "F05")]
    public void F05_LateButtonAfterScopeEndsCannotExecuteOrGrant(string transition, string answer)
    {
        var fixture = Fixture();
        var receipt = fixture.Request(Call());
        var decision = Assert.Single(fixture.PendingDecisions);
        switch (transition)
        {
            case "completed": fixture.End(); break;
            case "cancelled": fixture.CancelDecision(decision); break;
            case "new-prompt": fixture.Begin(Scope with { Prompt = 2 }); break;
            case "new-session": fixture.Begin(Scope with { Session = "session-b" }); break;
            case "new-connection": fixture.Begin(Scope with { Connection = 2 }); break;
        }
        Assert.False(fixture.Resolve(decision, answer == "once" ? Answer.Once : Answer.Chat));
        Assert.Equal(ApprovalState.Rejected, receipt.State);
        Assert.Empty(fixture.Invocations);
        Assert.Empty(fixture.PendingDecisions);
        Assert.Equal(0, fixture.ChatApprovalWrites);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("once")]
    [Trait("Contract", "F05")]
    public void F05_CompletedDecisionCannotBeUpgradedToAChatGrant(string firstAnswer)
    {
        var fixture = Fixture();
        fixture.Request(Call());
        var decision = Assert.Single(fixture.PendingDecisions);
        Assert.True(fixture.Resolve(decision, firstAnswer == "deny" ? Answer.Deny : Answer.Once));
        Assert.False(fixture.Resolve(decision, Answer.Chat));
        Assert.Equal(firstAnswer == "deny" ? 0 : 1, fixture.Invocations.Length);
        Assert.Equal(0, fixture.ChatApprovalWrites);
    }

    [Fact]
    [Trait("Contract", "F05")]
    public void F05_OldDecisionCannotApproveANewCallWithTheSameCallId()
    {
        var fixture = Fixture();
        fixture.Request(Call());
        var stale = Assert.Single(fixture.PendingDecisions);
        var nextOwner = Scope with { Connection = 2, Prompt = 2 };
        fixture.Begin(nextOwner);
        var next = Call() with { Owner = nextOwner };
        fixture.Request(next);
        var current = Assert.Single(fixture.PendingDecisions);
        Assert.NotEqual(stale, current);
        Assert.False(fixture.Resolve(stale, Answer.Chat));
        Assert.Empty(fixture.Invocations);
        Assert.Equal(0, fixture.ChatApprovalWrites);
        Assert.True(fixture.Resolve(current, Answer.Once));
        Assert.Single(fixture.Invocations);
        Assert.Equal(0, fixture.ChatApprovalWrites);
    }
}
