using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Headless;
using OneWare.Codex.Services;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using Xunit;

namespace OneWare.Codex.UnitTests;

public static class AcpFeatureLiveVerification
{
    // Opt-in runtime verification. The default test suite never invokes a model.
    public static async Task RunAsync(string appData, string evidence)
    {
        var profile = AcpProfile.Read(Path.GetFullPath(appData)) ??
                      throw new InvalidOperationException("Missing isolated profile.");
        Assert.Equal("gpt-6-luna", profile.Model);
        Assert.Equal("low", profile.Effort);
        Directory.CreateDirectory(evidence);
        var trace = new ConcurrentQueue<object>();
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AcpAdapterTests).Assembly).Dispatch(async () =>
        {
            await using var h = new AcpHarness(confirm: true, profile: profile,
                factory: () => new AcpLiveVerification.ObservedConnection(profile, trace));
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            h.Provider.RegisterFunction(new OneWareAiFunction
            {
                Name = "acp_feature_wait", FriendlyName = "ACP feature verification",
                Description = "Waits for the isolated verification gate, then returns WAIT_RELEASED. Call once.",
                IsReadOnly = true,
                ConfirmationCheck = _ => "Allow the isolated ACP feature wait tool?",
                Handler = (Func<CancellationToken, Task<string>>)(async token =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                    return "WAIT_RELEASED";
                })
            });
            h.Service.EventReceived += (_, e) =>
            {
                if (e is ChatPermissionRequestEvent permission &&
                    permission.Message == "Allow the isolated ACP feature wait tool?")
                {
                    trace.Enqueue(new { kind = "original_host_approval", permission.Message });
                    permission.AllowCommand.Execute(null);
                }

                if (e is ChatMessageEvent message) trace.Enqueue(new { kind = "final_message", message.Content });
                if (e is ChatErrorEvent error) trace.Enqueue(new { kind = "error", error.Message });
            };
            try
            {
                Assert.True(await h.Service.InitializeAsync());
                h.Agent = new ChatAgentDefinition
                {
                    Id = "plan", DisplayName = "Plan", TurnMode = ChatAgentTurnMode.Plan,
                    Model = "gpt-6-luna", ReasoningEffort = "low"
                };
                var unchanged = Path.Combine(h.Workspace, "plan-target.txt");
                File.WriteAllText(unchanged, "UNCHANGED");
                await h.Service
                    .SendAsync(
                        "Propose a two-step plan for documenting plan-target.txt. Assume a plain text document; no questions are needed. Do not use tools or edit anything. Include PLAN_ACP_OK in your plan. Keep it brief.")
                    .WaitAsync(TimeSpan.FromSeconds(150));
                Assert.Empty(h.Events.OfType<ChatErrorEvent>());
                Assert.Contains(h.Events.OfType<ChatMessageEvent>(), x => x.Content.Contains("PLAN_ACP_OK"));
                Assert.Empty(h.Starts);
                Assert.Equal("UNCHANGED", File.ReadAllText(unchanged));
                trace.Enqueue(new { kind = "plan_verified", h.Service.SelectionText, writes = 0 });

                h.Agent = new ChatAgentDefinition
                {
                    Id = "agent", DisplayName = "Agent",
                    Model = "gpt-6-luna", ReasoningEffort = "low"
                };
                h.Events.Clear();
                var active =
                    h.Service.SendAsync(
                        "Call mcp__oneware__acp_feature_wait exactly once, then answer FIRST_DONE. Do not call any other tools.");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(150));
                var path = Path.Combine(h.Workspace, "queued.txt");
                File.WriteAllText(path, "FILE_SNAPSHOT_ACP");
                var editor = new AcpTestSupport.EditorContext("before SELECTION_ACP after",
                    Path.Combine(h.Workspace, "selection.txt"));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                editor.Select(7, 13);
                Assert.Equal("SELECTION_ACP", editor.Editor.SelectedText);
                h.Document = editor.Document;
                h.Documents[editor.Path] = editor.Document;
                var image = Path.Combine(h.Workspace, "red.png");
                File.WriteAllBytes(image, Convert.FromBase64String(RedPng));
                h.Service.Attachments.AddFiles([path, image]);
                Assert.True(h.Service.TryAddImageAttachment(Convert.FromBase64String(BluePng), "image/png",
                    "blue.png"));
                Assert.Equal(4, h.Service.Attachments.Capture().Blocks.Length);
                await h.Service.SendAsync(
                    "Read the two attached text resources and identify the solid colour of each image in order. Reply exactly FILE_MARKER|SELECTION_MARKER|colour1|colour2 using their actual values. Do not call tools.",
                    ChatSendMode.Queue);
                File.WriteAllText(path, "CHANGED_AFTER_QUEUE");
                editor.Editor.Text = "CHANGED_AFTER_QUEUE";
                h.Document = null;

                await h.Service
                    .SendAsync(
                        "After the waiting tool returns, replace FIRST_DONE with STEER_ACP_OK. Reply only STEER_ACP_OK.",
                        ChatSendMode.Steer)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                trace.Enqueue(new { kind = "steer_acknowledged", activeSession = h.Service.CurrentSessionId });
                release.TrySetResult(true);
                await active.WaitAsync(TimeSpan.FromSeconds(180));
                Assert.Empty(h.Events.OfType<ChatErrorEvent>());
                Assert.Equal(1, calls);
                var messages = h.Events.OfType<ChatMessageEvent>().Select(x => x.Content).ToArray();
                Assert.Equal(2, messages.Length);
                Assert.Contains("STEER_ACP_OK", messages[0]);
                Assert.Contains("FILE_SNAPSHOT_ACP", messages[1]);
                Assert.Contains("SELECTION_ACP", messages[1]);
                Assert.Contains("red", messages[1].ToLowerInvariant());
                Assert.Contains("blue", messages[1].ToLowerInvariant());
                Assert.DoesNotContain("CHANGED_AFTER_QUEUE", messages[1]);
                Assert.Single(h.Events.OfType<ChatIdleEvent>());
                Assert.Equal("Codex ACP · gpt-6-luna · low", h.Service.SelectionText);
                trace.Enqueue(new
                {
                    kind = "queue_attachments_verified", session = h.Service.CurrentSessionId,
                    toolCalls = calls, responses = messages, idleEvents = 1, fileAndSelectionFrozen = true, images = 2
                });
                Console.WriteLine(
                    "ACP_FEATURES_LIVE_PASS: server Plan, original approval, injected Steer, queued file/selection/two images, Luna/low.");
                return true;
            }
            finally
            {
                release.TrySetResult(true);
                File.WriteAllText(Path.Combine(evidence, "features-live.json"),
                    JsonSerializer.Serialize(trace.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            }
        }, CancellationToken.None);
    }

    private const string RedPng =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAb0lEQVR4nO3PAQkAAAyEwO9feoshgnABdLep8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3IPanc8OLDQitxAAAAAElFTkSuQmCC";

    private const string BluePng =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAb0lEQVR4nO3PAQkAAAyEwO9feoshgnABdNvJ8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ2oPcf88OIhvJ6vAAAAAElFTkSuQmCC";
}