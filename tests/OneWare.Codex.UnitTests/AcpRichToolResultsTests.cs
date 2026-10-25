using System.Collections.ObjectModel;
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

public sealed class AcpRichToolResultsTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jM1sAAAAASUVORK5CYII=";
    private static object Image() => new { type = "image", mimeType = "image/png", data = Png };
    private static object Wrap(object block) => new { type = "content", content = block };
    private static object Text(string text) => Wrap(new { type = "text", text });
    private static object Diff(string? oldText, string newText, string path = "sample.txt") =>
        new { type = "diff", path, oldText, newText };
    private static object[] Mixed() => [Text("Before image"), Wrap(Image()), Diff("old\n", "new\n"), Text("After diff")];
    private static ChatMessageToolViewModel Tool(ChatSessionViewModel session)
    {
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(session.Messages.OfType<ChatMessageToolViewModel>());
    }

    [Fact]
    public void OrderedSnapshotsReplaceRetainAndClearWithoutRepeatingRawImageData()
    {
        var call = new AcpRuntimeToolCall();
        call.Update(AcpPeer.Json(new { content = Mixed(), rawOutput = new { result = Png } }));
        Assert.Equal(new[] { ChatToolContentKind.Text, ChatToolContentKind.Image, ChatToolContentKind.Diff, ChatToolContentKind.Text },
            call.Content!.Select(x => x.Kind));
        Assert.DoesNotContain(Png, call.Output);
        var snapshot = call.Content;
        call.Update(AcpPeer.Json(new { status = "in_progress", content = (object?)null }));
        Assert.Equal(snapshot, call.Content);
        call.Update(AcpPeer.Json(new { content = new[] { Text("Replacement") } }));
        Assert.Equal("Replacement", Assert.Single(call.Content!).Text);
        call.Update(AcpPeer.Json(new { rawOutput = "Final text result", status = "completed" }));
        Assert.Equal(new[] { "Replacement", "Final text result" }, call.Content!.Select(x => x.Text));
        call.Update(AcpPeer.Json(new { content = Array.Empty<object>() }));
        Assert.Empty(call.Content!);
        Assert.Equal("", call.Output);
        call.Update(AcpPeer.Json(new { status = "completed" }));
        Assert.Empty(call.Content!);
    }

    [Fact]
    public void OnlyKnownMcpEnvelopeConvertsImagesAndStandardContentWinsWithoutLosingErrors()
    {
        var call = new AcpRuntimeToolCall();
        var raw = new { result = new { content = new[] { Image() } }, error = "MCP reported an error" };
        call.Update(AcpPeer.Json(new { rawOutput = raw }));
        Assert.Null(call.Content);
        call.Update(AcpPeer.Json(new { rawInput = new { server = "fixture", tool = "image" }, rawOutput = raw }));
        Assert.Single(call.Content!, x => x.IsImage);
        Assert.Equal("MCP reported an error", call.Content!.Last().Text);
        call.Update(AcpPeer.Json(new { content = Enumerable.Range(0, 30).Select(i => Text("Segment " + i)).ToArray(), rawOutput = raw }));
        Assert.Equal("MCP reported an error", call.Content!.Last().Text);
        call.Update(AcpPeer.Json(new { content = new[] { Wrap(Image()) }, rawOutput = raw }));
        Assert.Single(call.Content!, x => x.IsImage);
        Assert.Equal("MCP reported an error", call.Content!.Last().Text);
        call.Update(AcpPeer.Json(new { rawOutput = new { result = new { content = new[] { new { type = "text", text = "alternate" } } } } }));
        Assert.Single(call.Content!, x => x.IsImage);
        Assert.DoesNotContain(call.Content!, x => x.Text == "alternate");
        var failure = new AcpRuntimeToolCall();
        failure.Update(AcpPeer.Json(new { content = Mixed(), rawOutput = "Partial failure", status = "failed" }));
        Assert.Equal("Partial failure", failure.Content!.Last().Text);
    }

    [Fact]
    public void UnsupportedMissingMalformedAndOverLimitResultsExplainWhyWithoutRawPayloads()
    {
        var call = new AcpRuntimeToolCall();
        call.Update(AcpPeer.Json(new { content = new object[]
        {
            Wrap(new { type = "image", mimeType = "image/png", data = "invalid" }),
            Wrap(new { type = "image", mimeType = "image/gif", data = Png }),
            Wrap(new { type = "image", mimeType = "image/jpeg" }),
            Wrap(new { type = "image", mimeType = "image/png", data = new string('x', ChatToolContentLimits.MaxImageBytes * 2) }),
            Wrap(new { type = "resource_link", name = "remote image", uri = "https://example.invalid/image.png" }),
            new { type = "terminal", terminalId = "terminal-1" }, new { type = "future" },
            Diff("old", new string('x', ChatToolContentLimits.MaxDiffCharacters + 1))
        } }));
        Assert.Equal(8, call.Content!.Count);
        Assert.All(call.Content!, x => { Assert.Equal(ChatToolContentKind.Notice, x.Kind); Assert.Null(x.Data); });
        Assert.Contains("invalid base64", call.Content![0].Text);
        Assert.Contains("remote image", call.Content![4].Text);
        var image = new ChatToolContent { Kind = ChatToolContentKind.Image, MimeType = "image/png", Data = Png };
        Assert.Null(ChatToolContentLimits.ValidateImage(image, out _, out var width, out var height));
        Assert.Equal(1, width); Assert.Equal(1, height);
        Assert.Equal(ChatToolContentKind.Notice, Assert.Single(ChatToolContentLimits.Normalize([image], 0)).Kind);
        var dimensions = Convert.FromBase64String(Png); dimensions[16] = 127;
        Assert.Contains("dimensions", ChatToolContentLimits.ValidateImage(image with { Data = Convert.ToBase64String(dimensions) }, out _, out _, out _));
        byte[] jpegHeader = [255, 216, 255, 192, 0, 17, 8, 0, 1, 0, 1, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0, 255, 217];
        Assert.Null(ChatToolContentLimits.ValidateImage(image with { MimeType = "image/jpeg", Data = Convert.ToBase64String(jpegHeader) }, out _, out width, out height));
        Assert.Equal(1, width); Assert.Equal(1, height);
    }

    [AvaloniaFact]
    public async Task ParentResultsAndReadOnlySegmentsSurviveReopenWithoutChangingFilesOrCallingTools()
    {
        await using var h = new AcpHarness(); var host = Host(h); var session = host.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        var path = Path.Combine(h.Workspace, "sample.txt"); await File.WriteAllTextAsync(path, "actual disk contents");
        session.CurrentMessage = "Rich tool fixture"; var send = session.SendCommand.ExecuteAsync(null);
        await h.Peer.PromptStarted.Task;
        var changes = new object[] { Wrap(Image()), Diff(null, "added", path), Diff("deleted", "", path),
            Diff("segment A", "segment B", path), Diff("", "", path) };
        h.Peer.Update(new { sessionUpdate = "tool_call", toolCallId = "rich", title = "Rich result", status = "pending", content = changes });
        Assert.True(Tool(session).IsToolRunning);
        h.Peer.Update(new { sessionUpdate = "tool_call_update", toolCallId = "rich", status = "completed" });
        h.Peer.Complete(); await send;
        var tool = Tool(session); Assert.True(tool.IsSuccessful);
        Assert.Equal(5, tool.ToolContent!.Count);
        Assert.Null(tool.ToolContent[1].OldText); Assert.Equal("", tool.ToolContent[2].NewText);
        Assert.Equal("segment A", tool.ToolContent[3].OldText);
        Assert.Equal("", tool.ToolContent[4].OldText);
        host.SaveState();
        var id = h.Service.CurrentSessionId!; await h.Service.DisposeAsync();
        var peer = new AcpPeer { Id = id };
        await using var service = new AcpChatService(h.Host, h.Profile, () => h.Agent, () => peer);
        var restoredHost = Host(h, service); var restored = restoredHost.CurrentSession!;
        await AcpHarness.Until(() => restored.IsConnected && peer.Resumes > 0);
        var restoredTool = Tool(restored);
        Assert.Equal(tool.ToolContent, restoredTool.ToolContent);
        Assert.False(restoredTool.IsToolRunning); Assert.Null(restoredTool.StopCommand);
        Assert.Empty(peer.Prompts);
        Assert.Equal("actual disk contents", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(peer.Requests, x => x.Method.Contains("tool"));
    }

    [AvaloniaFact]
    public async Task ParentAndChildCardsAreIsolatedAndCompletedChildRejectsLateImages()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "inspector", DisplayName = "Inspector", Description = "Inspect the fixture", Instructions = "Inspect", Model = "gpt-6-luna" });
        var host = Host(h); var session = host.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "Delegate preview"; var send = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        peers[0].Update(new { sessionUpdate = "tool_call", toolCallId = "same", title = "Parent", content = new[] { Text("parent") } });
        var delegated = peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool, new { agent = "inspector", task = "Inspect" });
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        peers[1].Update(new { sessionUpdate = "tool_call", toolCallId = "same", title = "Child", content = Mixed(), status = "completed" });
        Dispatcher.UIThread.RunJobs();
        var block = Assert.Single(session.Messages.OfType<ChatMessageSubAgentViewModel>());
        var child = Assert.Single(block.Items.OfType<ChatMessageToolViewModel>());
        Assert.Single(child.ToolContent!, x => x.IsImage);
        Assert.Equal("parent", Assert.Single(Tool(session).ToolContent!).Text);
        var count = h.Events.Count;
        peers[1].Update(new { sessionUpdate = "tool_call_update", toolCallId = "same", content = new[] { Text("LATE") } });
        peers[0].Update(new { sessionUpdate = "tool_call_update", toolCallId = "same", content = Mixed() }, "foreign");
        Assert.Equal(count, h.Events.Count);
        peers[1].Complete(); await delegated;
        peers[0].Update(new { sessionUpdate = "tool_call_update", toolCallId = "same", status = "failed", rawOutput = "parent error" });
        peers[0].Complete(); await send;
        Assert.False(Tool(session).IsSuccessful);
        Assert.Contains(Tool(session).ToolContent!, x => x.Text == "parent error");
        Assert.True(child.IsSuccessful);
        host.SaveState();
        var saved = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories)));
        Assert.Contains("OldText", saved); Assert.DoesNotContain("LATE", saved);
        var parentId = h.Service.CurrentSessionId!; await h.Service.DisposeAsync();
        var restoredPeer = new AcpPeer { Id = parentId };
        await using var restoredService = new AcpChatService(h.Host, h.Profile, () => h.Agent, () => restoredPeer);
        var restored = Host(h, restoredService).CurrentSession!;
        await AcpHarness.Until(() => restored.IsConnected && restoredPeer.Resumes > 0);
        Dispatcher.UIThread.RunJobs();
        var restoredChild = Assert.Single(Assert.Single(restored.Messages.OfType<ChatMessageSubAgentViewModel>())
            .Items.OfType<ChatMessageToolViewModel>());
        Assert.Equal(child.ToolContent, restoredChild.ToolContent);
        Assert.Empty(restoredPeer.Prompts);
    }

    [AvaloniaFact]
    public async Task HistoryCapsEmbeddedImagesAcrossToolsWithoutChangingLiveResults()
    {
        await using var h = new AcpHarness(); var host = Host(h); var session = host.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "Bound stored images"; var send = session.SendCommand.ExecuteAsync(null);
        await h.Peer.PromptStarted.Task;
        var bytes = new byte[ChatToolContentLimits.MaxImageBytes]; Convert.FromBase64String(Png).CopyTo(bytes, 0);
        var image = Wrap(new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(bytes) });
        for (var i = 0; i < 5; i++) h.Peer.Update(new
        {
            sessionUpdate = "tool_call", toolCallId = "image-" + i, title = "Image", status = "completed", content = new[] { image }
        });
        h.Peer.Complete(); await send; Dispatcher.UIThread.RunJobs(); host.SaveState();
        Assert.All(session.Messages.OfType<ChatMessageToolViewModel>(), x => Assert.True(Assert.Single(x.ToolContent!).IsImage));
        var saved = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(h.Workspace, "chat_*.json", SearchOption.AllDirectories)));
        Assert.Contains("stored preview size limit", saved);
        Assert.True(saved.Length < ChatToolContentLimits.MaxHistoryImageBytes * 4 / 3 + 100_000);
    }

    [AvaloniaFact]
    public async Task DetachingDropsQueuedAndLateToolSnapshots()
    {
        await using var h = new AcpHarness(); var host = Host(h); var session = host.CurrentSession!;
        await AcpHarness.Until(() => session.IsConnected);
        var send = h.Service.SendAsync("Queued tool"); await h.Peer.PromptStarted.Task;
        h.Peer.Update(new { sessionUpdate = "tool_call", toolCallId = "queued", content = Mixed() });
        host.ShowOverviewCommand.Execute(null);
        await h.Service.AbortAsync(); await send;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(session.Messages.OfType<ChatMessageToolViewModel>());
    }

    private static ChatViewModel Host(AcpHarness h, AcpChatService? service = null)
    {
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
        var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? Path.Combine(h.Workspace, "chat-host") : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent }, "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        chat.RegisterChatService(service ?? h.Service); chat.InitializeContent();
        if (chat.CurrentSession == null) chat.OpenNewChat();
        return chat;
    }
}
