using Xunit;
using static OneWare.Codex.AdapterContractTests.ApprovalTestSupport;

namespace OneWare.Codex.AdapterContractTests;

// Each control disables one gate in the SAME fixture and requires its specific forbidden effect.
// These are sensitivity checks of the synthetic oracle, never a product defect reproduction.
public sealed class NegativeControlTests
{
    [Fact]
    [Trait("Contract", "NegativeControl")]
    public void Detects_F05_StaleApprovalExecutionAndGrant()
    {
        var fixture = Fixture(acceptStaleDecision: true);
        fixture.Request(Call());
        var decision = Assert.Single(fixture.PendingDecisions);
        fixture.End();
        Assert.True(fixture.Resolve(decision, Answer.Chat));
        Assert.Single(fixture.Invocations);
        Assert.Equal(1, fixture.ChatApprovalWrites);
        Assert.NotNull(Record.Exception(() => Assert.Equal(0, fixture.ChatApprovalWrites)));
    }
}
