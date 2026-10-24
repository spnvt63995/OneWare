using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

[Trait("Contract", "F06")]
public sealed class InvocationPresentationTests
{
    [Fact]
    public void F06_InvocationProgressCompletionAndComparisonKeepOriginalIds()
    {
        var f = new PresentationFixture();
        var call = BoundaryData.Call("edit-a", "editFile");
        var row = f.Invoke(call, clientTool: true);
        Assert.Equal(call.Owner.Session, Assert.Single(f.Invocations).Owner.Session);
        Assert.Equal(call.CallId, row.Call.CallId);
        Assert.True(f.Progress(call, "half done"));
        Assert.True(f.ProposeEdit(call, "proposed text"));
        Assert.True(f.Return(call, "model result"));
        Assert.Equal("pending", row.Review);
        Assert.Equal("original text", f.DocumentText);
        Assert.Equal(0, row.Completions);
        Assert.True(f.Complete(call, originalClientEvent: true));
        Assert.Equal(1, row.Completions);
        Assert.Equal(call, Assert.Single(f.ModelResults).Call);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("progress")]
    [InlineData("completed")]
    [InlineData("permission")]
    public void F06_OwnBackendEchoDoesNotCreateRowsOrInvokeAgain(string kind)
    {
        var f = new PresentationFixture();
        var call = BoundaryData.Call();
        f.BackendEcho(call, kind);
        Assert.Empty(f.Rows);
        Assert.Empty(f.Invocations);
        var original = f.Invoke(call);
        f.BackendEcho(call, kind);
        f.Invoke(call);
        Assert.Same(original, Assert.Single(f.Rows));
        Assert.Single(f.Invocations);
        Assert.Equal(0, original.Completions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F06_OnlyTheCorrectCompletionOwnerCanCompleteOnce(bool clientTool)
    {
        var f = new PresentationFixture();
        var call = BoundaryData.Call();
        var row = f.Invoke(call, clientTool);
        Assert.False(f.Complete(call, !clientTool));
        Assert.True(f.Complete(call, clientTool));
        Assert.False(f.Complete(call, clientTool));
        Assert.Equal(1, row.Completions);
    }

    [Theory]
    [InlineData("call")]
    [InlineData("session")]
    [InlineData("connection")]
    [InlineData("prompt")]
    public void F06_ForeignProgressCannotReachTheSelectedRow(string mismatch)
    {
        var f = new PresentationFixture();
        var call = BoundaryData.Call();
        var row = f.Invoke(call);
        var foreign = mismatch switch
        {
            "call" => call with { CallId = "other" },
            "session" => call with { Owner = call.Owner with { Session = "other" } },
            "connection" => call with { Owner = call.Owner with { Connection = 2 } },
            _ => call with { Owner = call.Owner with { Prompt = 2 } }
        };
        Assert.False(f.Progress(foreign, "wrong progress"));
        Assert.False(f.ProposeEdit(foreign, "wrong edit"));
        Assert.Empty(row.Progress);
        Assert.Equal("none", row.Review);
    }

    [Fact]
    public void F06_LateResultOnlyCompletesItsOriginalRow()
    {
        var f = new PresentationFixture();
        var old = BoundaryData.Call();
        var oldRow = f.Invoke(old);
        var next = old with { Owner = old.Owner with { Prompt = 2 } };
        var nextRow = f.Invoke(next);
        Assert.True(f.Return(old, "old result"));
        Assert.Equal(1, oldRow.Completions);
        Assert.Equal(0, nextRow.Completions);
        Assert.Same(nextRow, f.Selected);
        Assert.Equal(old, Assert.Single(f.ModelResults).Call);
        Assert.False(f.Return(old, "duplicate result"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F06_OnlyExplicitReviewChangesTheInMemoryDocument(bool accept)
    {
        var f = new PresentationFixture();
        var call = BoundaryData.Call("edit-a", "editFile");
        var row = f.Invoke(call, true);
        f.ProposeEdit(call, "replacement");
        f.Return(call, "edit staged");
        f.Complete(call, true);
        Assert.Equal("original text", f.DocumentText);
        Assert.True(f.ReviewEdit(call, accept));
        Assert.Equal(accept ? "replacement" : "original text", f.DocumentText);
        Assert.Equal(accept ? "accepted" : "rolled-back", row.Review);
        Assert.False(f.ReviewEdit(call, !accept));
    }
}
