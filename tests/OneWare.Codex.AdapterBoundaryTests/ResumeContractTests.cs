using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

[Trait("Contract", "F08")]
public sealed class ResumeContractTests
{
    internal static void Load(SessionFixture f)
    {
        var ticket = f.BeginResume();
        Assert.True(f.FinishResume(ticket, new(f.Identity.Id, f.Identity.Directory, true, "observed-model", "observed-effort")));
    }

    [Fact]
    public void F08_SuccessBindsSameIdentityAndKeepsLocalHistory()
    {
        var f = new SessionFixture();
        var identity = f.Identity;
        var history = f.History.ToArray();
        Load(f);
        Assert.True(f.Loaded);
        Assert.Equal(identity, f.Identity);
        Assert.Equal(history, f.History);
        Assert.Equal(identity, Assert.Single(f.Requests).Identity);
        Assert.Empty(f.HistoryWrites);
        Assert.Empty(f.NewSessionRequests);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("wrong-id")]
    [InlineData("wrong-cwd")]
    public void F08_FailurePreservesHistoryAndRetryUsesSameId(string failure)
    {
        var f = new SessionFixture();
        var ticket = f.BeginResume();
        var reply = new ResumeReply(f.Identity.Id, f.Identity.Directory, true, "observed", "high");
        reply = failure switch
        {
            "failure" => reply with { Success = false },
            "wrong-id" => reply with { Id = "replacement" },
            _ => reply with { Directory = "/another/work" }
        };
        var history = f.History.ToArray();
        Assert.False(f.FinishResume(ticket, reply));
        Assert.False(f.Loaded);
        Assert.NotNull(f.Error);
        Assert.Equal(history, f.History);
        Assert.Empty(f.NewSessionRequests);
        Assert.Empty(f.HistoryWrites);
        Load(f);
        Assert.All(f.Requests, request => Assert.Equal("session-a", request.Identity.Id));
        Assert.True(f.Loaded);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("cwd")]
    [InlineData("connection")]
    public void F08_LateResponseCannotBindAnotherSelection(string mismatch)
    {
        var f = new SessionFixture();
        var old = f.BeginResume();
        if (mismatch == "connection") f.Reconnect();
        else f.Select(mismatch switch
        {
            "provider" => f.Identity with { Provider = "other" },
            "profile" => f.Identity with { Profile = "other" },
            "session" => f.Identity with { Id = "other" },
            _ => f.Identity with { Directory = "/other" }
        });
        var identity = f.Identity;
        Assert.False(f.FinishResume(old, new(old.Identity.Id, old.Identity.Directory, true, "old-model", "high")));
        Assert.Equal(identity, f.Identity);
        Assert.False(f.Loaded);
        Assert.Null(f.Observed);
        Assert.Empty(f.HistoryWrites);
        Assert.Empty(f.NewSessionRequests);
    }

    [Fact]
    public void F08_OlderSuccessOrFailureCannotUndoANewerLoadedRequest()
    {
        var f = new SessionFixture();
        var old = f.BeginResume();
        Load(f);
        var observed = f.Observed;
        Assert.False(f.FinishResume(old, new(f.Identity.Id, f.Identity.Directory, true, "old", "high")));
        Assert.False(f.FinishResume(old, new(f.Identity.Id, f.Identity.Directory, false, "old", "high")));
        Assert.True(f.Loaded);
        Assert.Equal(observed, f.Observed);
        Assert.Null(f.Error);
    }
}
