using System.Text.Json;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private readonly SemaphoreSlim _submission = new(1);
    private readonly SemaphoreSlim _steering = new(1);
    private readonly List<Prepared> _queue = [];
    private long _submissionEpoch;
    private Task _run = Task.CompletedTask;

    private sealed record Prepared(
        string Text,
        OneWareContext Context,
        bool Plan,
        bool ReadOnly,
        string[]? Allowed,
        string Model,
        string? Effort,
        string? AgentInstructions,
        string Additions,
        Dictionary<string, OneWareTool> Tools,
        long ToolsRevision,
        AcpPluginCatalog Plugins,
        AcpAttachments.Snapshot Content,
        long Epoch)
    {
        public string? AgentModel { get; init; }
        public string? AgentEffort { get; init; }
        public bool IsAutomatic { get; init; }
        public string? CompletionNotice { get; init; }
    }

    public Task SendAsync(string prompt) => SendAsync(prompt, ChatSendMode.Send);

    public async Task SendAsync(string text, ChatSendMode mode)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Task? run = null;
        await _submission.WaitAsync(_lifetime.Token);
        try
        {
            long epoch;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_family.AccountChanging)
                    throw new InvalidOperationException("Wait for the ACP account operation to finish.");
                epoch = _submissionEpoch;
                if (_sending != 0 && !_automaticTurn && mode == ChatSendMode.Send)
                    throw new InvalidOperationException("Codex ACP is working. Use Queue or Steer.");
            }

            var context = await _host.CaptureContextAsync();
            var plugins = await CapturePluginsAsync(text);
            var (tools, revision) = await CaptureToolsAsync();
            var input = await OneWareHost.Ui(() =>
            {
                var agent = _agent();
                return Task.FromResult(new Prepared(text, context,
                    agent?.TurnMode == ChatAgentTurnMode.Plan,
                    agent?.IsReadOnly == true || agent?.TurnMode == ChatAgentTurnMode.Plan,
                    agent?.Tools?.ToArray(), agent?.Model ?? _selection.Model,
                    agent?.ReasoningEffort ?? _selection.Effort, agent?.Instructions,
                    string.Join("\n\n", _host.GetPromptAdditions()), tools, revision, plugins,
                    Attachments.Capture(), epoch)
                {
                    AgentModel = agent?.Model, AgentEffort = agent?.ReasoningEffort
                });
            });

            if (mode == ChatSendMode.Steer)
            {
                await SteerAsync(input);
                return;
            }

            var queued = false;
            lock (_sync)
            {
                if (epoch != _submissionEpoch || _disposed)
                    throw new InvalidOperationException("The ACP conversation changed while preparing this message.");
                _automaticResultsPaused = false;
                if (_sending != 0)
                {
                    ValidateContent(input);
                    _queue.Add(input);
                    queued = true;
                }
                else
                {
                    _sending = 1;
                    PostOptions();
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _sendCancellation = cancellation;
                    run = _run = RunQueueAsync(input, cancellation);
                }
            }

            if (queued)
                await OneWareHost.Ui(() =>
                {
                    Attachments.Accept(input.Content);
                    return Task.FromResult(true);
                });
        }
        finally
        {
            _submission.Release();
        }

        if (run != null) await run;
    }

    public Task<bool> RemoveMostRecentQueuedMessageAsync()
    {
        lock (_sync)
        {
            if (_queue.Count == 0) return Task.FromResult(false);
            _queue.RemoveAt(_queue.Count - 1);
            return Task.FromResult(true);
        }
    }

    public Task<bool> ClearQueuedMessagesAsync()
    {
        lock (_sync) _queue.Clear();
        return Task.FromResult(true);
    }

    private void ValidateContent(Prepared input)
    {
        if (input.Content.Blocks.Length == 0) return;
        var prompt = _capabilities.TryGetProperty("promptCapabilities", out var capabilities) ? capabilities : default;
        foreach (var block in input.Content.Blocks)
        {
            var capability = Text(block, "type") == "image" ? "image" : "embeddedContext";
            if (prompt.ValueKind != JsonValueKind.Object || !prompt.TryGetProperty(capability, out var supported) ||
                supported.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("This ACP agent does not support " + capability + " attachments.");
        }
    }

    private object[] PromptBlocks(Prepared input, bool steer = false)
    {
        var text = steer
            ? "User steering request:\n" + input.Text + "\n\nOneWare context (reference data):\n" +
              JsonSerializer.Serialize(input.Context)
            : Instructions + "\n\n" + input.Additions + "\n\n" + input.AgentInstructions + input.Plugins.Describe() +
              DescribeBackgroundDelegations() + "\n" + QuestionInstructions +
              (input.Plan ? "\n" + PlanInstructions : "") +
              "\n\nOneWare context (reference data):\n" + JsonSerializer.Serialize(input.Context) +
              (input.IsAutomatic ? "\n\nContinue the existing conversation after the background work completed." :
                  "\n\nUser request:\n" + input.Text) + input.CompletionNotice;
        return new object[] { new { type = "text", text } }.Concat(input.Content.Blocks.Cast<object>()).ToArray();
    }

    private async Task RunQueueAsync(Prepared first, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        var entered = false;
        string? lastModel = null;
        Prepared? input = first;
        try
        {
            await _gate.WaitAsync(cancellation.Token);
            entered = true;
            while (input != null)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var directory = Path.GetFullPath(input.Context.Directory);
                lock (_sync)
                    if (input.Epoch != _submissionEpoch)
                        throw new OperationCanceledException(cancellation.Token);
                if (Path.GetFullPath((await _host.CaptureContextAsync()).Directory) != directory)
                    throw new InvalidOperationException(
                        "The workspace changed. Resubmit the waiting ACP message.");
                // A queued/automatic turn takes the latest confirmed user selection,
                // while explicit agent overrides and the running turn remain unchanged.
                await _optionsGate.WaitAsync(cancellation.Token);
                try
                {
                    input = input with
                    {
                        Model = input.AgentModel ?? _selection.Model,
                        Effort = input.AgentEffort ?? _selection.Effort
                    };
                    do
                    {
                        var (tools, revision) = await CaptureToolsAsync();
                        input = input with { Tools = tools, ToolsRevision = revision };
                        await EnsureSessionAsync(directory, input.Tools, cancellation.Token);
                        ValidateContent(input);
                        await SelectAsync("mode", "read-only", cancellation.Token);
                        await SelectAsync("model", input.Model, cancellation.Token);
                        if (input.Effort != null)
                            await SelectAsync("reasoning_effort", input.Effort, cancellation.Token);
                        // Collaboration mode is distinct from sandbox/approval mode.
                        await SelectAsync("collaboration_mode", input.Plan ? "plan" : "default", cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                    } while (input.ToolsRevision != Interlocked.Read(ref _toolsRevision));
                }
                finally { _optionsGate.Release(); }
                if (Path.GetFullPath((await _host.CaptureContextAsync()).Directory) != directory)
                    throw new InvalidOperationException("The workspace changed while preparing this prompt.");
                cancellation.Token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    input = AttachCompletedAgentsLocked(input);
                    if (input.IsAutomatic && input.CompletionNotice == null)
                    {
                        input = NextInputLocked();
                        if (input == null) FinishRun(cancellation, lastModel);
                        continue;
                    }
                    _automaticTurn = input.IsAutomatic;
                    if (_delegation == null && !input.IsAutomatic)
                        _continuationInput = input with
                        {
                            Content = new AcpAttachments.Snapshot([], []), CompletionNotice = null
                        };
                }
                var turn = new Prompt(_connection!, _generation, _session!, directory, input.ReadOnly,
                    input.Allowed, input.Tools, input.Model, input.ToolsRevision, cancellation.Token) { Input = input };
                lock (_sync) _prompt = turn;
                try
                {
                    await OneWareHost.Ui(() =>
                    {
                        SelectionText = $"Codex ACP · {input.Model} · {input.Effort}" + (input.Plan ? " · Plan" : "");
                        OnPropertyChanged(nameof(SelectionText));
                        RefreshOptionsProjection();
                        Attachments.Accept(input.Content);
                        return Task.FromResult(true);
                    });
                    lastModel = input.Model;
                    if (!input.IsAutomatic) Emit(new ChatUserMessageEvent(input.Text));
                    var reply = await turn.Connection.RequestAsync("session/prompt",
                        new { sessionId = turn.Session, prompt = PromptBlocks(input) }, turn.Cancellation.Token);
                    ObservePromptUsage(turn, reply);
                    lock (_sync) turn.Finished = true;
                    // A steering acknowledgement belongs before this turn's final/idle events.
                    await _steering.WaitAsync(cancellation.Token);
                    _steering.Release();
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (Text(reply, "stopReason") == "cancelled")
                        throw new OperationCanceledException("The ACP prompt was cancelled.", cancellation.Token);
                    if (Text(reply, "stopReason") != "end_turn")
                        throw new IOException("ACP prompt stopped: " + Text(reply, "stopReason"));
                    if (turn.Text.Length > 0)
                        Emit(new ChatMessageEvent(turn.Text.ToString(), turn.MessageId) { Model = input.Model });
                }
                finally
                {
                    lock (_sync)
                    {
                        turn.Finished = true;
                        turn.Cancellation.Cancel();
                        if (ReferenceEquals(_prompt, turn)) _prompt = null;
                    }

                    CancelForegroundFunctions();
                    if (turn.RestartRequired) await DisconnectAsync();
                }

                lock (_sync)
                {
                    input = NextInputLocked();
                    if (input == null) FinishRun(cancellation, lastModel);
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync) _automaticResultsPaused = true;
        }
        catch (Exception error)
        {
            lock (_sync) _automaticResultsPaused = true;
            if (input != null)
                await OneWareHost.Ui(() =>
                {
                    Attachments.Restore(input.Content);
                    return Task.FromResult(true);
                });
            Failure(error);
        }
        finally
        {
            lock (_sync)
            {
                FinishRun(cancellation, lastModel);
            }

            if (entered) _gate.Release();
            cancellation.Dispose();
        }
    }

    private void FinishRun(CancellationTokenSource owner, string? model)
    {
        if (!ReferenceEquals(_sendCancellation, owner)) return;
        _queue.Clear();
        _sendCancellation = null;
        _automaticTurn = false;
        Emit(new ChatIdleEvent { Model = model });
        _sending = 0;
        PostOptions();
    }

    private async Task SteerAsync(Prepared input)
    {
        await _steering.WaitAsync(_lifetime.Token);
        Prompt? turn = null;
        var dispatched = false;
        try
        {
            lock (_sync)
            {
                turn = _prompt;
                if (turn == null || !Current(turn) || !_supportsSteering || input.Epoch != _submissionEpoch)
                    throw new InvalidOperationException("Wait for an active ACP turn with steering support.");
                var original = turn.Input!;
                if (input.Plan != original.Plan || input.ReadOnly != original.ReadOnly ||
                    input.AgentModel != original.AgentModel || input.AgentEffort != original.AgentEffort ||
                    input.AgentInstructions != original.AgentInstructions ||
                    !Enumerable.SequenceEqual(input.Allowed ?? [], original.Allowed ?? []) ||
                    (input.Allowed == null) != (original.Allowed == null) ||
                    Path.GetFullPath(input.Context.Directory) != turn.Directory)
                    throw new InvalidOperationException(
                        "Steer keeps the active turn's agent, tools and model. Use Queue to change them.");
                ValidateContent(input);
            }

            dispatched = true;
            var response = await turn.Connection.RequestAsync("_session/steering",
                new { sessionId = turn.Session, prompt = PromptBlocks(input, steer: true) }, turn.Cancellation.Token);
            if (Text(response, "outcome") != "injected")
                throw new InvalidOperationException(
                    "The ACP turn ended before steering was accepted. Resubmit the message.");
            lock (_sync)
            {
                if (!ReferenceEquals(_prompt, turn) || turn.Cancellation.IsCancellationRequested ||
                    turn.Generation != _generation || input.Epoch != _submissionEpoch)
                    throw new OperationCanceledException();
                Emit(new ChatUserMessageEvent(input.Text));
            }

            await OneWareHost.Ui(() =>
            {
                Attachments.Accept(input.Content);
                return Task.FromResult(true);
            });
        }
        catch
        {
            if (dispatched && turn != null)
            {
                // The pinned extension can start a new turn after a completion race, without
                // returning that turn's final response. Never leave that unowned turn running.
                turn.RestartRequired = true;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await turn.Connection.NotifyAsync("session/cancel", new { sessionId = turn.Session },
                        deadline.Token);
                }
                catch (Exception)
                {
                }

                turn.Cancellation.Cancel();
                lock (_sync) _sendCancellation?.Cancel();
            }

            throw;
        }
        finally
        {
            _steering.Release();
        }
    }
}