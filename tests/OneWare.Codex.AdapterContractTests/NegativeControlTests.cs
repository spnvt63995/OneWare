using Xunit;
using static OneWare.Codex.AdapterContractTests.ContractTestSupport;

namespace OneWare.Codex.AdapterContractTests;

// Each control disables one gate in the SAME fixture and requires its specific forbidden effect.
// These are sensitivity checks of the synthetic oracle, never a product defect reproduction.
public sealed class NegativeControlTests
{
    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F05_StaleApprovalExecutionAndGrant()
    {
        var fixture = Fixture(approval: true, fault: Fault.AcceptStaleDecision);
        fixture.Request(Call());
        var decision = Assert.Single(fixture.PendingDecisions);
        fixture.End();
        Assert.True(fixture.Resolve(decision, Answer.Chat));
        Assert.Single(fixture.Invocations);
        Assert.Equal(1, fixture.ChatApprovalWrites);
        Assert.NotNull(Record.Exception(() => Assert.Equal(0, fixture.ChatApprovalWrites)));
    }

    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F09_ReplayReachingHistoryAndApproval()
    {
        var calls = Fixture(approval: true);
        var replay = new ReplayFixture(Scope, ReplayContractTests.Saved, calls, Fault.ReplayAsLive);
        var load = replay.BeginLoad();
        replay.ObserveMessage(Scope, load, new("unmapped-replay", "assistant", "past output"));
        replay.ObservePermission(Scope, load, Call());
        Assert.Equal(1, replay.Writes);
        Assert.Equal(1, calls.ApprovalRequests);
        Assert.NotNull(Record.Exception(() => Assert.Equal(0, replay.Writes)));
    }
}
