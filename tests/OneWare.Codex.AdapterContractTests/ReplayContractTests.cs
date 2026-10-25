using Xunit;
using static OneWare.Codex.AdapterContractTests.ContractTestSupport;

namespace OneWare.Codex.AdapterContractTests;

public sealed class ReplayContractTests
{
    internal static readonly Message[] Saved =
    [
        new("message-1", "user", "same text"),
        new("message-2", "assistant", "same text"),
        new("message-3", "user", "same text")
    ];

    [Fact]
    [Trait("Contract", "F09")]
    public void F09_LoadMessagesToolsCallbacksAndPermissionsHaveNoLiveSideEffects()
    {
        var calls = Fixture(approval: true);
        var replay = new ReplayFixture(Scope, Saved, calls);
        var load = replay.BeginLoad();
        foreach (var message in Saved.Reverse())
        {
            replay.ObserveMessage(Scope, load, message);
            replay.ObserveMessage(Scope, load, message);
        }
        replay.ObserveTool(Scope, load, Call());
        Assert.Equal(CallState.Rejected, replay.ObserveCallback(Scope, load, Call()).State);
        replay.ObservePermission(Scope, load, Call("old-permission"));
        Assert.True(replay.CompleteLoad(load, Scope));
        Assert.Equal(Saved, replay.History);
        Assert.Equal(0, replay.Writes);
        Assert.Equal(9, replay.Quarantined);
        NoSideEffects(calls);
    }

    [Theory]
    [InlineData("during-load-untagged")]
    [InlineData("after-load-tagged")]
    [InlineData("old-load-during-next-load")]
    [Trait("Contract", "F09")]
    public void F09_ReplayFenceSurvivesCompletionAndLoadGenerationChanges(string phase)
    {
        var calls = Fixture(approval: true);
        var replay = new ReplayFixture(Scope, Saved, calls);
        var first = replay.BeginLoad();
        long? tag = first;
        long? current = first;
        if (phase == "during-load-untagged") tag = null;
        else
        {
            Assert.True(replay.CompleteLoad(first, Scope));
            current = phase == "old-load-during-next-load" ? replay.BeginLoad() : null;
        }
        replay.ObserveMessage(Scope, tag, new("unmapped-replay-id", "assistant", "old output"));
        replay.ObserveTool(Scope, tag, Call());
        replay.ObserveCallback(Scope, tag, Call());
        replay.ObservePermission(Scope, tag, Call("permission"));
        Assert.Equal(Saved, replay.History);
        Assert.Equal(0, replay.Writes);
        NoSideEffects(calls);
        if (current != null) Assert.True(replay.CompleteLoad(current.Value, Scope));
    }

    [Theory]
    [InlineData("unknown-id")]
    [InlineData("missing-id")]
    [InlineData("conflicting-content")]
    [InlineData("foreign-session")]
    [InlineData("load-failure")]
    [Trait("Contract", "F09")]
    public void F09_UncertainOrFailedReplayPreservesTheOriginalHistory(string problem)
    {
        var calls = Fixture();
        var replay = new ReplayFixture(Scope, Saved, calls);
        var load = replay.BeginLoad();
        var message = problem switch
        {
            "unknown-id" => new Message("unknown", "assistant", "new-looking replay"),
            "missing-id" => new Message("", "assistant", "same text"),
            "conflicting-content" => Saved[0] with { Text = "different content" },
            _ => Saved[0]
        };
        replay.ObserveMessage(Scope, load, message);
        Assert.False(replay.CompleteLoad(load, problem == "foreign-session" ? Scope with { Session = "foreign" } : Scope,
            success: problem != "load-failure"));
        Assert.Equal(Saved, replay.History);
        Assert.Equal(0, replay.Writes);
        NoSideEffects(calls);
    }

    [Fact]
    [Trait("Contract", "F09")]
    public void F09_ForeignReplayAndLateCompletionCannotFinishTheCurrentLoad()
    {
        var calls = Fixture();
        var replay = new ReplayFixture(Scope, Saved, calls);
        var old = replay.BeginLoad();
        var current = replay.BeginLoad();
        var foreign = Scope with { Profile = "other-profile" };
        replay.ObserveMessage(foreign, current, new("foreign", "assistant", "same text"));
        replay.ObserveTool(foreign, current, Call() with { Owner = foreign });
        Assert.False(replay.CompleteLoad(old, Scope));
        Assert.True(replay.CompleteLoad(current, Scope));
        Assert.Equal(2, replay.Rejected);
        Assert.Equal(Saved, replay.History);
        NoSideEffects(calls);
    }

    [Fact]
    [Trait("Contract", "F09")]
    public void F09_LiveTrafficAfterLoadWorksWithoutContentBasedDeduplication()
    {
        var calls = Fixture();
        var replay = new ReplayFixture(Scope, Saved, calls);
        var load = replay.BeginLoad();
        foreach (var message in Saved) replay.ObserveMessage(Scope, load, message);
        Assert.True(replay.CompleteLoad(load, Scope));
        var live = new Message("message-4", "user", "same text");
        replay.ObserveMessage(Scope, null, live);
        replay.ObserveMessage(Scope, null, live);
        Assert.Equal(Saved.Append(live), replay.History);
        Assert.Equal(1, replay.Writes);
        replay.ObserveTool(Scope, null, Call());
        Assert.Equal(Call(), Assert.Single(calls.ToolNotices));
        Assert.Equal(CallState.Invoked, replay.ObserveCallback(Scope, null, Call()).State);
        Assert.Single(calls.Invocations);
        // Late replay must not rewrite the newly extended history or re-execute the tool.
        replay.ObserveMessage(Scope, load, live);
        replay.ObserveCallback(Scope, load, Call());
        Assert.Equal(1, replay.Writes);
        Assert.Single(calls.Invocations);
    }
}
