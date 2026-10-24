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

public sealed class AcpPluginTests
{
    [AvaloniaFact]
    public async Task InlineAndDirectorySkillsLoadOnDemandThroughTheOwnedMcpCall()
    {
        await using var h = new AcpHarness();
        await h.Service.RefreshOptionsAsync();
        RegisterSkills(h);
        var run = h.Service.SendAsync("Use plugin knowledge when needed.");
        await h.Peer.PromptStarted.Task;
        Assert.Equal(1, h.Peer.NewSessions);
        Assert.Equal(0, h.Peer.Resumes);
        var prompt = h.Peer.Prompts.Single().GetRawText();
        Assert.Contains("inline-guide", prompt);
        Assert.Contains("directory-guide", prompt);
        Assert.DoesNotContain("INLINE_SECRET_INSTRUCTIONS", prompt);
        Assert.DoesNotContain("DIRECTORY_SECRET_INSTRUCTIONS", prompt);
        var loaded = await h.Peer.CallTool("skill-inline", AcpPluginCatalog.LoadSkillTool,
            new { name = "inline-guide" });
        Assert.False(loaded.GetProperty("isError").GetBoolean());
        Assert.Contains("INLINE_SECRET_INSTRUCTIONS", loaded.GetRawText());
        var resource = await h.Peer.CallTool("skill-directory", AcpPluginCatalog.LoadSkillTool,
            new { name = "directory-guide" });
        Assert.Contains("DIRECTORY_SECRET_INSTRUCTIONS", resource.GetRawText());
        Assert.Contains("resource.txt", resource.GetRawText());
        Assert.Contains(Path.Combine(h.Workspace, "skills", "directory-guide"), resource.GetRawText());
        Assert.Equal(2, h.Events.OfType<ChatSkillLoadedEvent>().Count());
        Assert.Empty(h.Starts);
        var duplicate = await h.Peer.CallTool("skill-inline", AcpPluginCatalog.LoadSkillTool,
            new { name = "inline-guide" });
        Assert.True(duplicate.GetProperty("isError").GetBoolean());
        Assert.Equal(2, h.Events.OfType<ChatSkillLoadedEvent>().Count());
        var unknown = await h.Peer.CallTool("unknown-skill", AcpPluginCatalog.LoadSkillTool,
            new { name = "not-registered" });
        Assert.True(unknown.GetProperty("isError").GetBoolean());
        h.Peer.Complete();
        await run;
    }

    [AvaloniaFact]
    public async Task DelegatedStreamingSkillsToolsAndHistoryUseTheOriginalCopilotBlocks()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(confirm: true, factory: () =>
        {
            var peer = new AcpPeer { ModelChoices = ["gpt-6-luna", "plugin-model"] };
            peers.Add(peer); return peer;
        });
        RegisterSkills(h);
        h.Provider.RegisterAgent(new OneWareAiAgent
        {
            Name = "plugin-expert", DisplayName = "Plugin Expert", Description = "Check plugin fixtures.",
            Instructions = "PLUGIN_AGENT_INSTRUCTIONS", Skills = ["directory-guide"],
            Tools = ["acp_fixture"], Model = "plugin-model", ReasoningEffort = "medium"
        });
        var (chat, directory) = Host(h);
        var session = chat.OpenNewChat()!;
        await AcpHarness.Until(() => session.IsConnected);
        session.CurrentMessage = "Delegate a short plugin check.";
        var run = session.SendCommand.ExecuteAsync(null);
        await AcpHarness.Until(() => peers[0].Prompts.Count == 1);
        var delegation = peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool,
            new { agent = "plugin-expert", task = "CHILD_TASK" });
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var child = peers[1];
        Assert.Contains("PLUGIN_AGENT_INSTRUCTIONS", child.Prompts[0].GetRawText());
        Assert.Contains("DIRECTORY_SECRET_INSTRUCTIONS", child.Prompts[0].GetRawText());
        Assert.Contains(child.Requests, x => x.Method == "session/set_config_option" &&
            x.Parameters.GetProperty("configId").GetString() == "model" &&
            x.Parameters.GetProperty("value").GetString() == "plugin-model");
        Assert.Contains(child.Requests, x => x.Method == "session/set_config_option" &&
            x.Parameters.GetProperty("configId").GetString() == "reasoning_effort" &&
            x.Parameters.GetProperty("value").GetString() == "medium");
        child.Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "CHILD_ONLY:" } });
        var call = child.CallTool("child-tool", "acp_fixture", new { value = "value" });
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>()).AllowCommand.Execute(null);
        Assert.False((await call).GetProperty("isError").GetBoolean());
        Assert.Equal(peers[0].Id, Assert.Single(h.Starts).SessionId);
        child.Complete();
        Assert.Contains("CHILD_ONLY:answer", (await delegation).GetRawText());
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.IsBusy);
        var block = Assert.Single(session.Messages.OfType<ChatMessageSubAgentViewModel>());
        Assert.Equal("Plugin Expert", block.DisplayName);
        Assert.False(block.IsRunning);
        Assert.True(block.IsSuccessful);
        Assert.Contains(block.Items.OfType<ChatMessageSkillViewModel>(), x => x.SkillName == "directory-guide");
        Assert.Single(block.Items.OfType<ChatMessageToolViewModel>());
        Assert.Contains(block.Items.OfType<ChatMessageAssistantViewModel>(), x => x.Content == "CHILD_ONLY:answer");
        Assert.DoesNotContain(session.Messages.OfType<ChatMessageAssistantViewModel>(), x => x.Content.Contains("CHILD_ONLY"));
        Assert.DoesNotContain(h.Events.OfType<ChatUserMessageEvent>(), x => x.Content == "CHILD_TASK");
        Assert.Empty(h.Events.OfType<ChatIdleEvent>());
        Assert.False(child.IsConnected);
        peers[0].Complete();
        await run;
        Dispatcher.UIThread.RunJobs();
        chat.SaveState();
        var history = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "chat_*.json", SearchOption.AllDirectories)));
        Assert.Contains("Plugin Expert", history);
        Assert.Contains("directory-guide", history);
        Assert.Contains("CHILD_ONLY:answer", history);
        Assert.Equal("gpt-6-luna", h.Service.SelectedModelChoice?.Value);
    }

    [AvaloniaFact]
    public async Task ManualAgentsMissingSkillsAndInheritedToolRestrictionsAreEnforced()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        h.Agent = new ChatAgentDefinition { Id = "ask", DisplayName = "Ask", IsReadOnly = true, Tools = ["acp_fixture", "write_fixture"] };
        var writes = 0;
        h.Provider.RegisterFunction(new OneWareAiFunction { Name = "write_fixture", Description = "Writes.",
            Handler = (Func<string>)(() => (++writes).ToString()) });
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "manual-expert", Description = "Manual only.",
            Instructions = "Do the requested work.", Infer = false, Tools = ["acp_fixture", "write_fixture"] });
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "broken-expert", Description = "Missing prerequisite.",
            Instructions = "Do work.", Skills = ["missing"] });
        var first = h.Service.SendAsync("General question.");
        await AcpHarness.Until(() => peers.Count > 0 && peers[0].Prompts.Count == 1);
        Assert.DoesNotContain("manual-expert", peers[0].Prompts[0].GetRawText());
        Assert.True((await peers[0].CallTool("manual-denied", AcpPluginCatalog.DelegateTool,
            new { agent = "manual-expert", task = "Do work." })).GetProperty("isError").GetBoolean());
        Assert.True((await peers[0].CallTool("missing-skill", AcpPluginCatalog.DelegateTool,
            new { agent = "broken-expert", task = "Do work." })).GetProperty("isError").GetBoolean());
        Assert.Single(peers);
        peers[0].Complete(); await first;
        var second = h.Service.SendAsync("Use manual-expert for this task.");
        await AcpHarness.Until(() => peers[0].Prompts.Count == 2);
        var delegated = peers[0].CallTool("manual-allowed", AcpPluginCatalog.DelegateTool,
            new { agent = "manual-expert", task = "Write something." });
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        Assert.True((await peers[1].CallTool("write-denied", "write_fixture", new { })).GetProperty("isError").GetBoolean());
        Assert.True((await peers[1].CallTool("cycle", AcpPluginCatalog.DelegateTool,
            new { agent = "manual-expert", task = "Again." })).GetProperty("isError").GetBoolean());
        Assert.Equal(0, writes);
        Assert.False((await peers[1].CallTool("read-allowed", "acp_fixture", new { value = "value" })).GetProperty("isError").GetBoolean());
        peers[1].Complete(); await delegated;
        peers[0].Complete(); await second;
    }

    [AvaloniaFact]
    public async Task StopCancelsDelegatedApprovalsWithoutStoppingAnotherConversation()
    {
        var peers = new List<AcpPeer>();
        await using var h = new AcpHarness(confirm: true, factory: () => { var p = new AcpPeer(); peers.Add(p); return p; });
        h.Provider.RegisterAgent(new OneWareAiAgent { Name = "expert", Description = "Fixture work.", Instructions = "Use tools." });
        var first = h.Service.SendAsync("Delegate.");
        await AcpHarness.Until(() => peers.Count == 1 && peers[0].Prompts.Count == 1);
        var delegation = peers[0].CallTool("delegate", AcpPluginCatalog.DelegateTool, new { agent = "expert", task = "Read." });
        await AcpHarness.Until(() => peers.Count == 2 && peers[1].Prompts.Count == 1);
        var pending = peers[1].CallTool("waiting-tool", "acp_fixture", new { value = "value" });
        await AcpHarness.Until(() => h.Events.OfType<ChatPermissionRequestEvent>().Any());
        var decision = Assert.Single(h.Events.OfType<ChatPermissionRequestEvent>());
        await using var other = Assert.IsType<AcpChatService>(h.Service.CreateSession());
        var second = other.SendAsync("Independent.");
        await AcpHarness.Until(() => peers.Count == 3 && peers[2].Prompts.Count == 1);
        await h.Service.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await first;
        // Retiring the endpoint can close an HTTP response before its JSON body is written.
        try { await pending; } catch (Exception e) when (e is HttpRequestException or JsonException) { }
        try { await delegation; } catch (Exception e) when (e is HttpRequestException or JsonException) { }
        await AcpHarness.Until(() => !peers[1].IsConnected);
        decision.AllowCommand.Execute(null);
        Assert.Empty(h.Starts);
        Assert.True(peers[2].IsConnected);
        Assert.False(second.IsCompleted);
        Assert.Contains(h.Events.OfType<ChatSubAgentCompletedEvent>(), x => x.Cancelled);
        var before = h.Events.Count;
        peers[1].Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "STALE_CHILD" } });
        Assert.Equal(before, h.Events.Count);
        peers[2].Complete(); await second;
    }

    private static void RegisterSkills(AcpHarness h)
    {
        h.Provider.RegisterSkill(new OneWareAiSkill { Name = "inline-guide", Description = "Inline fixture guidance.",
            Instructions = "INLINE_SECRET_INSTRUCTIONS" });
        var root = Path.Combine(h.Workspace, "skills");
        var directory = Path.Combine(root, "directory-guide");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: 'directory-guide'\ndescription: >-\n  Directory fixture\n  guidance.\n---\nDIRECTORY_SECRET_INSTRUCTIONS Read resource.txt relative to this skill.\n");
        File.WriteAllText(Path.Combine(directory, "resource.txt"), "RESOURCE_CONTENT");
        h.Provider.RegisterSkillDirectory(root);
    }

    private static (ChatViewModel Chat, string Directory) Host(AcpHarness h)
    {
        var dock = InterfaceStub.Create<IMainDockService>((_, _) => null);
        var settings = InterfaceStub.Create<ISettingsService>((name, _) => name == "GetSettingValue" ? 50 : null);
        var directory = Path.Combine(h.Workspace, "chat-host");
        var paths = InterfaceStub.Create<IPaths>((name, _) => name == "get_AppDataDirectory" ? directory : null);
        var agent = new ChatAgentDefinition { Id = "agent", DisplayName = "Agent" };
        var agents = InterfaceStub.Create<IChatAgentService>((name, _) => name switch
        {
            "get_Agents" => new ObservableCollection<ChatAgentDefinition> { agent },
            "get_SelectedAgent" => agent, _ => null
        });
        var chat = new ChatViewModel(h.Provider, dock, new AiFileEditService(dock, settings), paths, settings,
            InterfaceStub.Create<IApplicationStateService>((_, _) => null), agents);
        chat.RegisterChatService(h.Service); chat.InitializeContent();
        return (chat, directory);
    }
}
