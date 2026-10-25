using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OneWare.Chat.Services;
using OneWare.Chat.ViewModels;
using OneWare.Chat.ViewModels.ChatMessages;
using OneWare.Codex.Services;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace OneWare.Codex.UnitTests;

public sealed class AcpToolDetailsTests
{
    private static object[] Content(string text) => [new { type = "content", content = new { type = "text", text } }];

    [AvaloniaFact]
    public async Task ChildCardsKeepInputProgressAndFailuresAcrossFollowupsWithoutDuplicateOrLateUpdates()
    {
        var peers = new List<AcpPeer>();
        var next = new Queue<AcpPeer>();
        await using var h = new AcpHarness(factory: () =>
        {
            var peer = next.Count > 0 ? next.Dequeue() : new AcpPeer();
            peers.Add(peer);
            return peer;
        });
        h.Provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "inspector", DisplayName = "Inspector", Description = "Synthetic tool display fixture.",
            Instructions = "Inspect the requested fixture.", Model = "gpt-6-luna", ReasoningEffort = "low"
        });
        var (chat, session) = Host(h);
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "ACP tool details fixture";
        var first = session.SendCommand.ExecuteAsync(null);
        await Ready(peers, 1);
        var delegated = peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool,
            new { agent = "inspector", task = "Inspect sample.txt" });
        await Ready(peers, 2);
        var child = peers[1];
        child.Update(new { sessionUpdate = "tool_call", toolCallId = "inspect", title = "Inspect workspace", status = "pending" });
        child.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", status = "in_progress",
            rawInput = new { command = "inspect sample.txt", limit = 3 },
            locations = new[] { new { path = "sample.txt", line = 12 } }, content = Content("Reading sample…") });
        Dispatcher.UIThread.RunJobs();
        var block = Assert.Single(session.Messages.OfType<ChatMessageSubAgentViewModel>());
        var tool = Assert.Single(block.Items.OfType<ChatMessageToolViewModel>());
        Assert.True(tool.IsToolRunning);
        Assert.Contains("command: inspect sample.txt", tool.ToolOutput);
        Assert.Contains("sample.txt:12", tool.ToolOutput);
        Assert.Contains("Reading sample…", tool.ToolOutput);
        child.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", content = Content("Inspection complete: 3 entries.") });
        child.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", status = "completed", rawInput = (object?)null });
        var count = h.Events.Count;
        child.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", status = "failed", rawOutput = "LATE_RESULT" });
        child.Update(new { sessionUpdate = "tool_call", toolCallId = "inspect", title = "Duplicate" });
        Assert.Equal(count, h.Events.Count);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(block.Items.OfType<ChatMessageToolViewModel>());
        Assert.True(tool.IsSuccessful);
        Assert.False(tool.IsToolRunning);
        Assert.DoesNotContain("Reading sample", tool.ToolOutput);
        Assert.Equal(1, tool.ToolOutput!.Split("Inspection complete").Length - 1);
        child.Complete();
        var id = Payload(await delegated).GetProperty("id").GetString()!;
        peers[0].Complete(); await first;

        var resumed = new AcpPeer { Id = child.Id };
        next.Enqueue(resumed);
        session.CurrentMessage = "Continue with the same inspector.";
        var second = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        var follow = peers[0].CallTool("follow", AcpPluginCatalog.FollowupTool, new { id, task = "Inspect missing.txt" });
        await Ready(peers, 3);
        resumed.Update(new { sessionUpdate = "tool_call", toolCallId = "inspect", title = "Inspect missing file",
            rawInput = new { path = "missing.txt" }, content = Content("Opening file…") });
        count = h.Events.Count;
        resumed.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", rawOutput = "FOREIGN_SESSION" }, Guid.NewGuid().ToString());
        child.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", rawOutput = "OLD_CONNECTION" });
        Assert.Equal(count, h.Events.Count);
        resumed.Update(new { sessionUpdate = "tool_call_update", toolCallId = "inspect", status = "failed",
            rawOutput = "Could not open missing.txt: file not found." });
        resumed.Complete(); await follow;
        peers[0].Complete(); await second;
        Dispatcher.UIThread.RunJobs();
        var blocks = session.Messages.OfType<ChatMessageSubAgentViewModel>().ToArray();
        Assert.Equal(2, blocks.Length);
        var failed = Assert.Single(blocks[1].Items.OfType<ChatMessageToolViewModel>());
        Assert.NotEqual(tool.SourceToolCallId, failed.SourceToolCallId);
        Assert.False(failed.IsSuccessful);
        Assert.Contains("path: missing.txt", failed.ToolOutput);
        Assert.Contains("file not found", failed.ToolOutput);
        Assert.DoesNotContain("Opening file", failed.ToolOutput);
        Assert.DoesNotContain("missing.txt", tool.ToolOutput);
        Assert.All(h.Events.OfType<ChatSubAgentCompletedEvent>(), x => Assert.Equal(1, x.TotalToolCalls));
        Assert.All(h.Events.OfType<ChatToolExecutionCompleteEvent>().Where(x => x.AgentId != null),
            x => Assert.False(string.IsNullOrEmpty(x.Output)));
        chat.SaveState();
        var history = Assert.Single(Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories));
        var saved = await File.ReadAllTextAsync(history);
        Assert.Contains("Inspection complete", saved);
        Assert.Contains("file not found", saved);
        Assert.DoesNotContain("OLD_CONNECTION", saved);
        Console.WriteLine("ACP_TOOL_DETAILS_FIXTURE: " + history);
    }

    [Fact]
    public void PartialSnapshotsRetainNullFieldsAndBoundTextWithoutInventingUnsupportedContent()
    {
        var call = new AcpRuntimeToolCall();
        call.Update(AcpPeer.Json(new { rawInput = new { command = new string('x', 300), nested = new { value = 1 } },
            content = Content(new string('y', 5000)), locations = new[] { new { path = "sample.txt", line = "invalid" } } }));
        var detail = call.Detail;
        var output = call.Output;
        Assert.Contains(new string('x', 200) + "…", detail);
        Assert.Equal(new string('y', 4000) + "…", output);
        call.Update(AcpPeer.Json(new { status = "completed", rawInput = (object?)null, rawOutput = (object?)null }));
        Assert.Equal(detail, call.Detail);
        Assert.Equal(output, call.Output);
        call.Update(AcpPeer.Json(new { content = new object[]
        {
            new { type = "content", content = new { type = "image", data = "UNSUPPORTED_IMAGE" } },
            new { type = "diff", path = "sample.txt", oldText = "old", newText = "UNSUPPORTED_DIFF" },
            new { type = "terminal", terminalId = "UNSUPPORTED_TERMINAL" }
        }}));
        Assert.Equal("", call.Output);
        Assert.Equal(new[] { ChatToolContentKind.Notice, ChatToolContentKind.Diff, ChatToolContentKind.Notice },
            call.Content!.Select(x => x.Kind));
        Assert.Equal(detail, call.Detail);
    }

    [AvaloniaTheory]
    [InlineData("false")]
    [InlineData("[1,2]")]
    [InlineData("{\"error\":\"Tool refused\"}")]
    public async Task InitialTerminalNotificationsCarryRawResultsOnce(string json)
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("Inspect fixture.");
        await h.Peer.PromptStarted.Task;
        var raw = JsonDocument.Parse(json).RootElement.Clone();
        h.Peer.Update(new { sessionUpdate = "tool_call", toolCallId = "immediate", title = "Inspect", status = "failed", rawOutput = raw });
        var complete = Assert.Single(h.Events.OfType<ChatToolExecutionCompleteEvent>());
        Assert.Equal(raw.ToString(), complete.Output);
        Assert.False(complete.Success);
        h.Peer.Update(new { sessionUpdate = "tool_call_update", toolCallId = "immediate", status = "completed", rawOutput = "late" });
        Assert.Single(h.Events.OfType<ChatToolExecutionCompleteEvent>());
        h.Peer.Complete(); await send;
    }

    [AvaloniaFact]
    public async Task HostToolsKeepTheirExistingDisplayAndApprovalPath()
    {
        await using var h = new AcpHarness(confirm: true);
        var send = h.Service.SendAsync("Run the fixture."); await h.Peer.PromptStarted.Task;
        var call = h.Peer.CallTool("client", "acp_fixture", new { value = "host" });
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        Assert.False((await call).GetProperty("isError").GetBoolean());
        h.Peer.Update(new { sessionUpdate = "tool_call_update", toolCallId = "client", status = "completed", rawOutput = "duplicate" });
        Assert.Single(h.Starts);
        Assert.Empty(h.Events.OfType<ChatToolExecutionUpdateEvent>());
        h.Peer.Complete(); await send;
    }

    [AvaloniaFact]
    public async Task CancellationRejectsLateToolProgress()
    {
        await using var h = new AcpHarness();
        var send = h.Service.SendAsync("Inspect fixture."); await h.Peer.PromptStarted.Task;
        h.Peer.Update(new { sessionUpdate = "tool_call", toolCallId = "cancelled", title = "Inspect" });
        await h.Service.AbortAsync(); await send;
        var count = h.Events.Count;
        h.Peer.Update(new { sessionUpdate = "tool_call_update", toolCallId = "cancelled", status = "failed", rawOutput = "late" });
        Assert.Equal(count, h.Events.Count);
    }

    private static Task Ready(List<AcpPeer> peers, int count) =>
        AcpHarness.Until(() => peers.Count >= count && peers[count - 1].Prompts.Count == 1);
    private static JsonElement Payload(JsonElement response)
    {
        Assert.False(response.GetProperty("isError").GetBoolean());
        return JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }

    private static (ChatViewModel Chat, ChatSessionViewModel Session) Host(AcpHarness h)
    {
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
        var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? Path.Combine(h.Workspace, "chat-host") : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
            "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        chat.RegisterChatService(h.Service); chat.InitializeContent();
        return (chat, chat.OpenNewChat()!);
    }
}
