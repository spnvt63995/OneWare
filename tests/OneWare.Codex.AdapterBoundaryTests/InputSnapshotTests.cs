using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

[Trait("Contract", "F10")]
public sealed class InputSnapshotTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void F10_ResumeObservationDoesNotOverwriteExplicitOrInheritedIntent(bool modelExplicit, bool effortExplicit)
    {
        var f = new SessionFixture();
        f.Saved = f.Saved with
        {
            Model = modelExplicit ? new(true, "chosen-model") : Intent.Inherit,
            Reasoning = effortExplicit ? new(true, "low") : Intent.Inherit
        };
        var saved = f.Saved;
        ResumeContractTests.Load(f);
        Assert.Equal(saved, f.Saved);
        Assert.Equal(("observed-model", "observed-effort"), f.Observed);
        var payload = f.OptionsPayload();
        Assert.Equal(modelExplicit, payload.ContainsKey("model"));
        Assert.Equal(effortExplicit, payload.ContainsKey("reasoning"));
        if (modelExplicit) Assert.Equal("chosen-model", payload["model"]);
        if (effortExplicit) Assert.Equal("low", payload["reasoning"]);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("cwd")]
    [InlineData("reconnect")]
    public void F10_ChangedScopeRequiresReviewInsteadOfSilentlySendingQueuedInput(string change)
    {
        var f = new SessionFixture();
        ResumeContractTests.Load(f);
        var queued = f.Queue("queued");
        if (change == "reconnect") f.Reconnect();
        else f.Select(change switch
        {
            "provider" => f.Identity with { Provider = "other" },
            "profile" => f.Identity with { Profile = "other" },
            "session" => f.Identity with { Id = "other" },
            _ => f.Identity with { Directory = "/other" }
        });
        ResumeContractTests.Load(f);
        Assert.False(f.Dispatch(queued));
        Assert.Empty(f.Dispatched);
        Assert.Equal(queued, Assert.Single(f.ReviewRequired));
    }

    [Fact]
    public void F10_OnlyControlledSameSessionRefreshCarriesQueuedInput()
    {
        var f = new SessionFixture();
        ResumeContractTests.Load(f);
        var queued = f.Queue("queued");
        f.Reconnect(controlledRefresh: true);
        Assert.False(f.Dispatch(queued)); // Must wait for verified same-ID resume.
        ResumeContractTests.Load(f);
        Assert.True(f.Dispatch(queued));
        Assert.Equal(queued, Assert.Single(f.Dispatched));
        f.Reconnect();
        ResumeContractTests.Load(f);
        Assert.False(f.Dispatch(queued)); // The ordinary reconnect clears the controlled bridge.
        Assert.Single(f.Dispatched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F10_NextPromptAcceptsQueuedInputButReviewsOldSteer(bool steer)
    {
        var f = new SessionFixture();
        ResumeContractTests.Load(f);
        var queued = f.Queue("next", steer);
        f.NextPrompt();
        Assert.Equal(!steer, f.Dispatch(queued));
        Assert.Equal(steer ? 1 : 0, f.ReviewRequired.Count);
        Assert.Equal(steer ? 0 : 1, f.Dispatched.Count);
    }
}
