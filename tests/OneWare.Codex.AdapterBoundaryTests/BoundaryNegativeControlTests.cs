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

    [Fact]
    public void Detects_F06_LateCompletionRoutedToTheCurrentRow()
    {
        var f = new PresentationFixture(routeToSelectedFault: true);
        var old = BoundaryData.Call();
        var oldRow = f.Invoke(old);
        var next = f.Invoke(old with { Owner = old.Owner with { Prompt = 2 } });
        f.Complete(old, false);
        Assert.Equal(1, next.Completions);
        Assert.Equal(0, oldRow.Completions);
        Assert.NotNull(Record.Exception(() => Assert.Equal(0, next.Completions)));
    }

    [Fact]
    public void Detects_F08_ReplacingSavedHistoryOnResume()
    {
        var f = new SessionFixture(replaceHistoryFault: true);
        var original = f.History.ToArray();
        ResumeContractTests.Load(f);
        Assert.NotEqual(original[0], f.History[0]);
        Assert.NotNull(Record.Exception(() => Assert.Equal(original, f.History)));
    }

    [Fact]
    public void Detects_F10_ObservedValuesOverwritingInheritedIntent()
    {
        var f = new SessionFixture(replaceIntentFault: true);
        ResumeContractTests.Load(f);
        Assert.True(f.Saved.Model.Explicit);
        Assert.NotNull(Record.Exception(() => Assert.False(f.Saved.Model.Explicit)));
    }

    [Fact]
    public void Detects_F11_IgnoringBackendDescriptionAndReadOnlyMetadata()
    {
        var f = new CatalogFixture(ignoreMetadataFault: true);
        f.Register(BoundaryData.Tool());
        f.ObserveBackend = offered => offered.ToDictionary(x => x.Key, x => x.Value with { Description = "stale", ReadOnly = false });
        Assert.True(f.Synchronize());
        Assert.True(f.Ready);
        Assert.NotNull(Record.Exception(() => Assert.False(f.Ready)));
    }

    [Fact]
    public void Detects_F12_ExecutingAnArchivedHandlerAfterRemoval()
    {
        var f = new CatalogFixture(archivedAliasFault: true);
        var called = 0;
        var old = BoundaryData.Tool(handler: _ => ++called);
        f.SaveLegacyDefinition(old);
        Assert.True(f.Synchronize());
        Assert.True(f.InvokeLegacy("oneware_readFile", BoundaryData.Json("""{"path":"a"}""")));
        Assert.Equal(1, called);
        Assert.NotNull(Record.Exception(() => Assert.Equal(0, called)));
    }
}
