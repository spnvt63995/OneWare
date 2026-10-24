using Xunit;
using static OneWare.Codex.AdapterContractTests.CallContractTests;

namespace OneWare.Codex.AdapterContractTests;

// Each control disables one gate in the SAME fixture and requires its specific forbidden effect.
// These are sensitivity checks of the synthetic oracle, never a product defect reproduction.
public sealed class NegativeControlTests
{
    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F01_ExecutionBeforeTheBackendNotice()
    {
        var fixture = Fixture(fault: Fault.RunBeforeMatch);
        fixture.Request("rpc-a", Call());
        Assert.Single(fixture.Invocations); // F01 requires zero at this exact checkpoint.
        Assert.NotNull(Record.Exception(() => Assert.Empty(fixture.Invocations)));
    }

    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F02_IgnoringTheActiveOwner()
    {
        var fixture = Fixture(fault: Fault.IgnoreOwner);
        var foreign = Call() with { Owner = Scope with { Session = "foreign" } };
        fixture.Notice(foreign);
        fixture.Request("rpc-a", foreign);
        Assert.Equal("foreign", Assert.Single(fixture.Invocations).SessionId);
        Assert.NotNull(Record.Exception(() => Assert.Empty(fixture.Invocations)));
    }

    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F03_ForgettingCompletedLogicalCalls()
    {
        var fixture = Fixture(fault: Fault.ForgetCompletedCall);
        fixture.Notice(Call());
        fixture.Request("rpc-a", Call());
        fixture.Finish(Call());
        fixture.Request("rpc-b", Call());
        Assert.Equal(2, fixture.Invocations.Length);
        Assert.NotNull(Record.Exception(() => Assert.Single(fixture.Invocations)));
    }

    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F05_StaleApprovalExecutionAndGrant()
    {
        var fixture = Fixture(approval: true, fault: Fault.AcceptStaleDecision);
        fixture.Notice(Call());
        fixture.Request("rpc-a", Call());
        var decision = Assert.Single(fixture.PendingDecisions);
        fixture.End();
        Assert.True(fixture.Resolve(decision, Answer.Chat));
        Assert.Single(fixture.Invocations);
        Assert.Equal(1, fixture.GrantCount);
        Assert.NotNull(Record.Exception(() => Assert.Equal(0, fixture.GrantCount)));
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
