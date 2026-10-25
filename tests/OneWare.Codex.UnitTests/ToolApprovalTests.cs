using System.Text.Json;
using OneWare.Codex.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public class ToolApprovalTests
{
    [Theory]
    [InlineData("ls -la", new[] { "ls" })]
    [InlineData("git status", new[] { "git status" })]
    [InlineData("git status && git diff --stat", new[] { "git status", "git diff" })]
    [InlineData("git -C repo status", new[] { "git" })]
    [InlineData("cd src; ls | grep foo", new[] { "cd", "ls", "grep" })]
    [InlineData("make 2>&1 | tail -n 20", new[] { "make", "tail" })]
    [InlineData("FOO=1 BAR=\"a b\" dotnet test -v minimal", new[] { "dotnet test" })]
    [InlineData("/usr/bin/python3 script.py", new[] { "python3" })]
    [InlineData("echo \"a; rm -rf /\"", new[] { "echo" })]
    [InlineData("Get-ChildItem -Recurse | Select-Object Name", new[] { "Get-ChildItem", "Select-Object" })]
    [InlineData("git status --short", new[] { "git status" })]
    [InlineData("git diff --stat", new[] { "git diff" })]
    [InlineData("git log -1", new[] { "git log" })]
    [InlineData("pwd", new[] { "pwd" })]
    [InlineData("git status && pwd", new[] { "git status", "pwd" })]
    [InlineData("git status | cat", new[] { "git status", "cat" })]
    [InlineData("pwd && git status --short", new[] { "pwd", "git status" })]
    [InlineData("pwd && whoami", new[] { "pwd", "whoami" })]
    public void GetCommandPrefixes_ReturnsTheCommandsThatRun(string command, string[] expected)
    {
        Assert.Equal(expected, ToolApproval.GetCommandPrefixes(command));
    }

    [Theory]
    [InlineData("echo $(rm -rf /)")]
    [InlineData("echo `whoami`")]
    [InlineData("sudo apt install foo")]
    [InlineData("bash -c 'rm -rf /'")]
    [InlineData("ls | xargs rm")]
    [InlineData("find . -name '*.o' -delete")]
    [InlineData("(cd src && make)")]
    [InlineData("ls\nrm -rf /")]
    [InlineData("echo \"unterminated")]
    [InlineData("   ")]
    [InlineData("sh -c 'pwd'")]
    [InlineData("echo $(pwd)")]
    [InlineData("pwd\nwhoami")]
    [InlineData("find . -exec pwd ;")]
    [InlineData("sudo git status")]
    [InlineData("echo <(pwd)")]
    [InlineData("echo 'unbalanced")]
    [InlineData("")]
    public void GetCommandPrefixes_RefusesCommandsItCannotJudge(string command)
    {
        Assert.Null(ToolApproval.GetCommandPrefixes(command));
    }

    [Theory]
    [InlineData("git status && ls", new[] { "tool:runTerminalCommand:git status", "tool:runTerminalCommand:ls" },
        "Allow \"git status\", \"ls\" in this chat")]
    [InlineData("git status && pwd", new[] { "tool:runTerminalCommand:git status", "tool:runTerminalCommand:pwd" },
        "Allow \"git status\", \"pwd\" in this chat")]
    public void ForTool_ScopesTerminalApprovalsToTheCommands(string command, string[] expectedKeys, string expectedLabel)
    {
        var args = JsonSerializer.SerializeToElement(new { command });

        var scope = ToolApproval.ForTool(ToolApproval.TerminalToolName, "Execute In Terminal", args);

        Assert.NotNull(scope);
        Assert.Equal(expectedKeys, scope.Keys);
        Assert.Equal(expectedLabel, scope.Label);
    }

    [Fact]
    public void ForTool_ReadsArgumentsEncodedAsString()
    {
        var args = JsonSerializer.SerializeToElement("""{"command":"ls"}""");

        var scope = ToolApproval.ForTool(ToolApproval.TerminalToolName, null, args);

        Assert.Equal(["tool:runTerminalCommand:ls"], scope?.Keys);
    }

    [Fact]
    public void ForTool_OffersNoChatApprovalForUnclearCommands()
    {
        var args = JsonDocument.Parse("""{"command":"sudo rm -rf /"}""").RootElement;

        Assert.Null(ToolApproval.ForTool(ToolApproval.TerminalToolName, null, args));
    }

    [Fact]
    public void ForTool_ApprovesOtherToolsAsAWhole()
    {
        var scope = ToolApproval.ForTool("compileProject", "Compile Project", null);

        Assert.Equal(["tool:compileProject"], scope?.Keys);
        Assert.Equal("Allow Compile Project in this chat", scope?.Label);
    }
}
