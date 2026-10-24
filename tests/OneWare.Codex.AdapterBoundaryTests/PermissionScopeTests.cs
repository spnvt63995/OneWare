using System.Text.Json;
using OneWare.Codex.Services;
using Xunit;

namespace OneWare.Codex.AdapterBoundaryTests;

[Trait("Contract", "F04")]
public sealed class PermissionScopeTests
{
    private static FunctionSpec Terminal => BoundaryData.Tool("runTerminalCommand") with { ReadOnly = false, Confirmation = _ => "original check" };
    private static BoundaryInvocation Command(string command, string id = "a") =>
        BoundaryData.Call(id, "runTerminalCommand") with { Arguments = JsonSerializer.SerializeToElement(new { command }) };

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void F04_OriginalConditionDecidesRegardlessOfReadOnlyHint(bool readOnly, bool hasCheck, bool asks)
    {
        var fixture = new PermissionFixture();
        var checkedCount = 0;
        var function = BoundaryData.Tool() with
        {
            ReadOnly = readOnly,
            Confirmation = hasCheck ? args => { checkedCount++; Assert.Equal("a.txt", args.GetProperty("path").GetString()); return asks ? "ask" : null; } : null
        };
        var result = fixture.Request(function, BoundaryData.Call());
        Assert.Equal(hasCheck ? 1 : 0, checkedCount);
        Assert.Equal(asks ? "pending" : "executed", result.State);
        Assert.Equal(asks ? 1 : 0, fixture.ApprovalRequests);
        Assert.Equal(asks ? 0 : 1, fixture.Invocations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F04_OnceAndDenyNeverGrantTheNextCall(bool allow)
    {
        var f = new PermissionFixture();
        Assert.True(f.Resolve(f.Request(Terminal, Command("git status")), allow ? PermissionAnswer.Once : PermissionAnswer.Deny));
        Assert.Equal(allow ? 1 : 0, f.Invocations.Count);
        Assert.Equal(0, f.GrantCount);
        Assert.Equal("pending", f.Request(Terminal, Command("git status", "b")).State);
    }

    [Fact]
    public void F04_GeneralGrantUsesTheNativeToolNameOnly()
    {
        var f = new PermissionFixture();
        var a = BoundaryData.Tool() with { Confirmation = _ => "ask" };
        Assert.True(f.Resolve(f.Request(a, BoundaryData.Call()), PermissionAnswer.Chat));
        Assert.Equal("executed", f.Request(a, BoundaryData.Call("b")).State);
        var b = a with { Name = "editFile" };
        Assert.Equal("pending", f.Request(b, BoundaryData.Call("c", "editFile")).State);
        Assert.Equal(1, f.GrantCount);
    }

    [Theory]
    [InlineData("git diff --stat")]
    [InlineData("git log -1")]
    [InlineData("pwd")]
    [InlineData("git status && pwd")]
    [InlineData("git status | cat")]
    public void F04_CommandGrantDoesNotExpandToAnotherPrefix(string other)
    {
        var f = new PermissionFixture();
        f.Resolve(f.Request(Terminal, Command("git status")), PermissionAnswer.Chat);
        Assert.Equal("executed", f.Request(Terminal, Command("git status --short", "b")).State);
        Assert.Equal("pending", f.Request(Terminal, Command(other, "c")).State);
        Assert.Equal(2, f.Invocations.Count);
    }

    [Fact]
    public void F04_CompoundCommandNeedsAllPrefixesAndPreservesTheOriginalLabel()
    {
        var f = new PermissionFixture();
        var call = Command("git status && pwd");
        var scope = ToolApproval.ForTool(Terminal.Name, Terminal.Name, call.Arguments)!;
        Assert.Equal(new[] { "tool:runTerminalCommand:git status", "tool:runTerminalCommand:pwd" }, scope.Keys);
        Assert.Equal("Allow \"git status\", \"pwd\" in this chat", scope.Label);
        f.Resolve(f.Request(Terminal, call), PermissionAnswer.Chat);
        Assert.Equal("executed", f.Request(Terminal, Command("pwd && git status --short", "b")).State);
        Assert.Equal("pending", f.Request(Terminal, Command("pwd && whoami", "c")).State);
    }

    [Theory]
    [InlineData("sh -c 'pwd'")]
    [InlineData("echo $(pwd)")]
    [InlineData("pwd\nwhoami")]
    [InlineData("find . -exec pwd ;")]
    [InlineData("sudo git status")]
    [InlineData("echo <(pwd)")]
    [InlineData("echo 'unbalanced")]
    [InlineData("")]
    public void F04_IndeterminateCommandsHaveNoChatGrantButCanBeAllowedOnce(string command)
    {
        var f = new PermissionFixture();
        var pending = f.Request(Terminal, Command(command));
        Assert.Null(pending.Keys);
        Assert.False(f.Resolve(pending, PermissionAnswer.Chat));
        Assert.Empty(f.Invocations);
        Assert.Equal(0, f.GrantCount);
        Assert.True(f.Resolve(pending, PermissionAnswer.Once));
        Assert.Single(f.Invocations);
        Assert.Equal("pending", f.Request(Terminal, Command(command, "b")).State);
    }

    [Theory]
    [InlineData("new-chat")]
    [InlineData("load")]
    [InlineData("reconnect")]
    [InlineData("controlled-foreign-session")]
    public void F04_GrantsResetOutsideTheSameSessionControlledRefresh(string transition)
    {
        var f = new PermissionFixture();
        f.Resolve(f.Request(Terminal, Command("git status")), PermissionAnswer.Chat);
        var owner = BoundaryData.Owner with { Connection = 2 };
        if (transition is "new-chat" or "controlled-foreign-session") owner = owner with { Session = "session-b" };
        f.Transition(owner, transition.StartsWith("controlled", StringComparison.Ordinal));
        Assert.Equal(0, f.GrantCount);
        Assert.Equal("pending", f.Request(Terminal, Command("git status", "b") with { Owner = owner }).State);
    }

    [Fact]
    public void F04_ControlledRefreshKeepsTheSameSessionGrant()
    {
        var f = new PermissionFixture();
        f.Resolve(f.Request(Terminal, Command("git status")), PermissionAnswer.Chat);
        var owner = BoundaryData.Owner with { Connection = 2 };
        f.Transition(owner, controlledRefresh: true);
        Assert.Equal("executed", f.Request(Terminal, Command("git status", "b") with { Owner = owner }).State);
    }

    [Fact]
    public void F04_ApprovalPinsTheOriginalDelegateAndArguments()
    {
        var called = new List<string>();
        var f = new PermissionFixture();
        var original = Terminal with { Handler = call => { called.Add(call.Arguments.GetProperty("command").GetString()!); return null; } };
        var pending = f.Request(original, Command("git status"));
        original = original with { Handler = _ => { called.Add("replacement"); return null; } };
        Assert.True(f.Resolve(pending, PermissionAnswer.Once));
        Assert.Equal(new[] { "git status" }, called);
        Assert.NotSame(original, pending.Function);
        Assert.False(f.Resolve(pending, PermissionAnswer.Chat));
    }

    [Fact]
    public void F04_PolicyFailureNeverCallsTheOriginalConditionOrHandler()
    {
        var checks = 0;
        var f = new PermissionFixture();
        var tool = Terminal with { Confirmation = _ => { checks++; return "ask"; } };
        Assert.Equal("denied", f.Request(tool, Command("git status"), policyAllows: false).State);
        Assert.Equal(0, checks);
        Assert.Equal(0, f.ApprovalRequests);
        Assert.Empty(f.Invocations);
    }
}
