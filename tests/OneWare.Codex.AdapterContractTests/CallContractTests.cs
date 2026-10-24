using Xunit;

namespace OneWare.Codex.AdapterContractTests;

public sealed class CallContractTests
{
    internal static readonly Owner Scope = new("fixture-acp", "isolated-profile", 1, "session-a", 1);
    internal static ToolCall Call(string id = "call-a") => new(Scope, id, "readFile", "{\"path\":\"fixture.txt\",\"lines\":[1,2]}");
    internal static ContractFixture Fixture(bool approval = false, Fault fault = Fault.None)
    {
        var fixture = new ContractFixture(approval, fault);
        fixture.Begin(Scope);
        return fixture;
    }

    [Theory]
    [InlineData("http-first")]
    [InlineData("event-first")]
    [Trait("Contract", "F01")]
    public void F01_BothArrivalOrdersWaitForTheSameCall(string order)
    {
        var fixture = Fixture();
        var notice = Call();
        // Object member order is irrelevant; array order and native argument values are preserved.
        var request = notice with { Arguments = "{\"lines\":[1,2],\"path\":\"fixture.txt\"}" };
        Receipt receipt;
        if (order == "http-first")
        {
            receipt = fixture.Request("rpc-a", request);
            Assert.Equal(CallState.Waiting, receipt.State);
            Assert.Empty(fixture.Invocations);
            Assert.Equal(0, fixture.ApprovalRequests);
            Assert.True(fixture.Notice(notice));
        }
        else
        {
            Assert.True(fixture.Notice(notice));
            Assert.Empty(fixture.Invocations);
            Assert.Equal(0, fixture.ApprovalRequests);
            receipt = fixture.Request("rpc-a", request);
        }
        Assert.Equal(CallState.Executing, receipt.State);
        Assert.Equal(new Invocation(Scope.Session, "call-a", "readFile", request.Arguments), Assert.Single(fixture.Invocations));
        Assert.True(fixture.Finish(request));
        Assert.Equal(CallState.Succeeded, receipt.State);
    }

    [Theory]
    [InlineData("http-first")]
    [InlineData("event-first")]
    [Trait("Contract", "F01")]
    public void F01_ApprovalCannotAppearUntilMatchingCompletes(string order)
    {
        var fixture = Fixture(approval: true);
        var call = Call();
        Receipt receipt;
        if (order == "http-first")
        {
            receipt = fixture.Request("rpc-a", call);
            Assert.Empty(fixture.PendingDecisions);
            fixture.Notice(call);
        }
        else
        {
            fixture.Notice(call);
            Assert.Empty(fixture.PendingDecisions);
            receipt = fixture.Request("rpc-a", call);
        }
        Assert.Empty(fixture.Invocations);
        Assert.Equal(CallState.Approval, receipt.State);
        Assert.Equal(1, fixture.ApprovalRequests);
        Assert.True(fixture.Resolve(Assert.Single(fixture.PendingDecisions), Answer.Once));
        Assert.Single(fixture.Invocations);
        Assert.Equal(0, fixture.GrantCount);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("connection")]
    [InlineData("prompt")]
    [InlineData("missing-session")]
    [InlineData("missing-connection")]
    [Trait("Contract", "F02")]
    public void F02_AgreeingPacketsFromAnotherOwnerStillCannotExecute(string field)
    {
        var fixture = Fixture(approval: true);
        var owner = field switch
        {
            "provider" => Scope with { Provider = "another-provider" },
            "profile" => Scope with { Profile = "another-profile" },
            "session" => Scope with { Session = "another-session" },
            "connection" => Scope with { Connection = 2 },
            "prompt" => Scope with { Prompt = 2 },
            "missing-session" => Scope with { Session = "" },
            "missing-connection" => Scope with { Connection = 0 },
            _ => throw new ArgumentException(field)
        };
        var call = Call() with { Owner = owner };
        Assert.False(fixture.Notice(call));
        Assert.Equal(CallState.Rejected, fixture.Request("rpc-a", call).State);
        NoSideEffects(fixture);
    }

    [Theory]
    [InlineData("call-id")]
    [InlineData("missing-call-id")]
    [InlineData("server")]
    [InlineData("tool")]
    [InlineData("missing-tool")]
    [InlineData("arguments")]
    [InlineData("array-order")]
    [InlineData("malformed-json")]
    [InlineData("non-object")]
    [InlineData("duplicate-json-key")]
    [InlineData("nested-duplicate-key")]
    [Trait("Contract", "F02")]
    public void F02_MismatchedOrIncompleteCallCannotExecute(string field)
    {
        var fixture = Fixture(approval: true);
        var original = Call();
        fixture.Notice(original);
        var altered = field switch
        {
            "call-id" => original with { CallId = "unannounced-call" },
            "missing-call-id" => original with { CallId = "" },
            "server" => original with { Server = "other-server" },
            "tool" => original with { Tool = "writeFile" },
            "missing-tool" => original with { Tool = "" },
            "arguments" => original with { Arguments = "{\"path\":\"other.txt\",\"lines\":[1,2]}" },
            "array-order" => original with { Arguments = "{\"path\":\"fixture.txt\",\"lines\":[2,1]}" },
            "malformed-json" => original with { Arguments = "{" },
            "non-object" => original with { Arguments = "null" },
            "duplicate-json-key" => original with { Arguments = "{\"path\":\"a\",\"path\":\"b\"}" },
            "nested-duplicate-key" => original with { Arguments = "{\"nested\":{\"key\":1,\"key\":2}}" },
            _ => throw new ArgumentException(field)
        };
        var receipt = fixture.Request("rpc-a", altered);
        fixture.Advance(5);
        Assert.Contains(receipt.State, new[] { CallState.Rejected, CallState.Expired });
        NoSideEffects(fixture);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("connection")]
    [InlineData("prompt")]
    [InlineData("call-id")]
    [InlineData("tool")]
    [InlineData("arguments")]
    [Trait("Contract", "F02")]
    public void F02_HttpFirstCannotBindAForeignOrMismatchedNotice(string field)
    {
        var fixture = Fixture(approval: true);
        var call = Call();
        var receipt = fixture.Request("rpc-a", call);
        var notice = field switch
        {
            "session" => call with { Owner = Scope with { Session = "foreign" } },
            "connection" => call with { Owner = Scope with { Connection = 2 } },
            "prompt" => call with { Owner = Scope with { Prompt = 2 } },
            "call-id" => call with { CallId = "other-call" },
            "tool" => call with { Tool = "writeFile" },
            "arguments" => call with { Arguments = "{\"path\":\"other.txt\"}" },
            _ => throw new ArgumentException(field)
        };
        fixture.Notice(notice);
        fixture.Advance(5);
        Assert.Contains(receipt.State, new[] { CallState.Rejected, CallState.Expired });
        Assert.False(fixture.Notice(call));
        NoSideEffects(fixture);
    }

    [Theory]
    [InlineData("event-missing")]
    [InlineData("http-missing")]
    [Trait("Contract", "F02")]
    public void F02_MissingCounterpartExpiresAndLateArrivalCannotReviveIt(string missing)
    {
        var fixture = Fixture();
        var call = Call();
        var receipt = missing == "event-missing" ? fixture.Request("rpc-a", call) : null;
        if (missing == "http-missing") fixture.Notice(call);
        fixture.Advance(4);
        NoSideEffects(fixture);
        if (receipt != null) Assert.Equal(CallState.Waiting, receipt.State);
        fixture.Advance(1);
        if (receipt != null)
        {
            Assert.Equal(CallState.Expired, receipt.State);
            Assert.False(fixture.Notice(call));
        }
        else Assert.Equal(CallState.Rejected, fixture.Request("rpc-a", call).State);
        NoSideEffects(fixture);
    }

    [Theory]
    [InlineData("completed-prompt")]
    [InlineData("new-prompt")]
    [InlineData("new-connection")]
    [Trait("Contract", "F02")]
    public void F02_PendingCallbackCannotMoveIntoANewerScope(string transition)
    {
        var fixture = Fixture();
        var receipt = fixture.Request("rpc-a", Call());
        if (transition == "completed-prompt") fixture.End();
        else fixture.Begin(transition == "new-prompt" ? Scope with { Prompt = 2 } : Scope with { Connection = 2 });
        Assert.False(fixture.Notice(Call()));
        Assert.Equal(CallState.Rejected, receipt.State);
        NoSideEffects(fixture);
    }

    [Fact]
    [Trait("Contract", "F02")]
    public void F02_ConflictingNoticeCannotReplaceArgumentsWhileApprovalIsPending()
    {
        var fixture = Fixture(approval: true);
        fixture.Notice(Call());
        var receipt = fixture.Request("rpc-a", Call());
        var decision = Assert.Single(fixture.PendingDecisions);
        Assert.False(fixture.Notice(Call() with { Arguments = "{\"path\":\"swapped.txt\"}" }));
        Assert.False(fixture.Resolve(decision, Answer.Chat));
        Assert.Equal(CallState.Rejected, receipt.State);
        Assert.Empty(fixture.Invocations);
        Assert.Equal(0, fixture.GrantCount);
    }

    [Theory]
    [InlineData("same", "pending")]
    [InlineData("different", "pending")]
    [InlineData("same", "executing")]
    [InlineData("different", "executing")]
    [InlineData("same", "success")]
    [InlineData("different", "success")]
    [InlineData("same", "failure")]
    [InlineData("different", "failure")]
    [Trait("Contract", "F03")]
    public void F03_DuplicateCallbacksNeverStartASecondInvocation(string rpc, string phase)
    {
        var fixture = Fixture();
        var call = Call();
        var first = fixture.Request("rpc-a", call);
        if (phase != "pending") fixture.Notice(call);
        if (phase is "success" or "failure") Assert.True(fixture.Finish(call, phase == "success"));
        var previousState = first.State;
        var duplicate = fixture.Request(rpc == "same" ? "rpc-a" : "rpc-b", call);
        Assert.Equal(CallState.Rejected, duplicate.State);
        Assert.Equal(previousState, first.State);
        if (phase == "pending") fixture.Notice(call);
        Assert.Single(fixture.Invocations);
        Assert.Equal(0, fixture.GrantCount);
    }

    [Fact]
    [Trait("Contract", "F03")]
    public void F03_DuplicatesDuringApprovalDoNotCreateAnotherDecision()
    {
        var fixture = Fixture(approval: true);
        fixture.Notice(Call());
        fixture.Request("rpc-a", Call());
        fixture.Request("rpc-b", Call());
        fixture.Notice(Call());
        Assert.Equal(1, fixture.ApprovalRequests);
        Assert.True(fixture.Resolve(Assert.Single(fixture.PendingDecisions), Answer.Once));
        Assert.Single(fixture.Invocations);
    }

    [Fact]
    [Trait("Contract", "F03")]
    public async Task F03_ConcurrentHttpIdsClaimTheLogicalCallOnce()
    {
        var fixture = Fixture();
        fixture.Notice(Call());
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
            Task.Run(() => fixture.Request($"rpc-{i}", Call()))));
        Assert.Single(fixture.Invocations);
        Assert.Single(results, x => x.State == CallState.Executing);
        Assert.Equal(31, results.Count(x => x.State == CallState.Rejected));
        Assert.True(fixture.Finish(Call()));
        Assert.False(fixture.Finish(Call()));
    }

    [Fact]
    [Trait("Contract", "F03")]
    public void F03_DistinctCallsRemainExecutableButRpcIdReuseIsRejected()
    {
        var fixture = Fixture();
        fixture.Notice(Call());
        fixture.Request("rpc-a", Call());
        fixture.Finish(Call());
        var second = Call("call-b");
        fixture.Notice(second);
        Assert.Equal(CallState.Rejected, fixture.Request("rpc-a", second).State);
        Assert.Single(fixture.Invocations);
        Assert.Equal(CallState.Executing, fixture.Request("rpc-b", second).State);
        Assert.Equal(new[] { "call-a", "call-b" }, fixture.Invocations.Select(x => x.ToolCallId));
    }

    internal static void NoSideEffects(ContractFixture fixture)
    {
        Assert.Empty(fixture.Invocations);
        Assert.Equal(0, fixture.ApprovalRequests);
        Assert.Equal(0, fixture.GrantCount);
    }
}
