using System.Text.Json;
using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

// Sensitivity checks of the contract oracles; these are not reproductions of product defects.
[Trait("Contract", "NegativeControl")]
public sealed class BoundaryNegativeControlTests
{
    [Fact]
    public void Detects_F04_ApprovingOnlyOneCompoundPrefix()
    {
        var f = new PermissionFixture(anyPrefixFault: true);
        var tool = BoundaryData.Tool("runTerminalCommand") with { Confirmation = _ => "ask" };
        BoundaryInvocation Call(string command, string id) => BoundaryData.Call(id, tool.Name) with { Arguments = JsonSerializer.SerializeToElement(new { command }) };
        f.Resolve(f.Request(tool, Call("git status", "a"), ["tool:runTerminalCommand:git status"]), PermissionAnswer.Chat);
        f.Request(tool, Call("git status && pwd", "b"), ["tool:runTerminalCommand:git status", "tool:runTerminalCommand:pwd"]);
        Assert.Equal(2, f.Invocations.Count);
        Assert.NotNull(Record.Exception(() => Assert.Single(f.Invocations)));
    }
}
