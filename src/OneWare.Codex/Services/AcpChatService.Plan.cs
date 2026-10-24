using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private const string PlanInstructions = """
        Plan this request. Do not modify files or run modifying tools.
        This is ACP: instructions to call exit_plan_mode refer to another backend; that tool is unavailable here.
        Instead, finish a complete plan in a <proposed_plan> block, with the opening and closing tags on their own lines.
        The ACP runtime will present that plan for the user's Start implementation or Update plan decision.
        Never implement before that decision.
        """;

    private async Task<object> ReviewPlanAsync(IAgentConnection connection, long generation,
        JsonElement parameters, CancellationToken token)
    {
        var cancelled = new { outcome = new { outcome = "cancelled" } };
        // codex-acp's PlanReviewReporter uses a switch_mode permission with these exact
        // choices. A shell, file, MCP or unrelated mode permission must never enter this path.
        if (!parameters.TryGetProperty("toolCall", out var call) ||
            Text(call, "kind") != "switch_mode" || Text(call, "status") != "pending" ||
            Text(call, "toolCallId") is not { } id || !id.StartsWith("plan-review:", StringComparison.Ordinal) ||
            id.Length == "plan-review:".Length ||
            !call.TryGetProperty("rawInput", out var raw) || Text(raw, "plan") is not { } plan ||
            string.IsNullOrWhiteSpace(plan) ||
            !parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array ||
            !options.EnumerateArray().Any(x => Text(x, "optionId") == "implement_plan" && Text(x, "kind") == "allow_once") ||
            !options.EnumerateArray().Any(x => Text(x, "optionId") == "revise_plan" && Text(x, "kind") == "reject_once"))
            return cancelled;

        Prompt? turn;
        lock (_sync)
        {
            turn = _prompt;
            if (turn == null || !Current(turn) || !ReferenceEquals(connection, turn.Connection) ||
                generation != turn.Generation || Text(parameters, "sessionId") != turn.Session ||
                turn.Input is not { Plan: true, Command: null } || _delegation != null || _agents == null ||
                !turn.PlanReviews.Add(id)) return cancelled;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, turn.Cancellation.Token);
        using var decision = new PendingDecision<bool>(cancellation.Token);
        ChatPlanReadyEvent? offer = null;
        ChatAgentDefinition? planningAgent = null;
        var answered = false;
        bool CurrentChoice() => Current(turn) && !cancellation.IsCancellationRequested &&
                                ReferenceEquals(_agent(), planningAgent);
        var start = new RelayCommand<Control?>(_ => Decide(true), _ => CurrentChoice() && decision.IsPending);
        var update = new RelayCommand<Control?>(_ => Decide(false), _ => CurrentChoice() && decision.IsPending);

        void Decide(bool implement)
        {
            lock (_sync)
                if (CurrentChoice()) decision.TryComplete(implement);
        }

        try
        {
            var shown = await OneWareHost.Ui(() =>
            {
                lock (_sync)
                {
                    planningAgent = _agent();
                    if (!CurrentChoice() || planningAgent?.TurnMode != ChatAgentTurnMode.Plan)
                        return Task.FromResult(false);
                    // The runtime continues implementation inside the same session/prompt request.
                    // Finish the planning message first so later output has its own transcript block.
                    if (turn.Text.Length > 0)
                        Emit(new ChatMessageEvent(turn.Text.ToString(), turn.MessageId) { Model = turn.Model });
                    turn.Text.Clear();
                    turn.MessageId = Guid.NewGuid().ToString("N");
                    offer = new ChatPlanReadyEvent("The plan is ready. Start the implementation or have it changed.",
                        plan, start, update);
                    Emit(offer);
                    return Task.FromResult(true);
                }
            });
            if (!shown) return cancelled;
            var implement = await decision.Task;
            var context = await _host.CaptureContextAsync();
            answered = await OneWareHost.Ui(() =>
            {
                lock (_sync)
                {
                    if (!CurrentChoice() || Path.GetFullPath(context.Directory) != turn.Directory) return Task.FromResult(false);
                    if (implement)
                    {
                        if (!_agents.SelectAgent(BuiltInChatAgents.Agent)) return Task.FromResult(false);
                        // Only this explicit decision lifts Plan's read-only restriction. Keep the
                        // originating model and tool allowlist; subsequent requests use the live approval mode.
                        turn.ReadOnly = false;
                        turn.Input = turn.Input! with
                        {
                            Plan = false, ReadOnly = false, AgentInstructions = _agents.SelectedAgent?.Instructions,
                            AgentModel = _agents.SelectedAgent?.Model,
                            AgentEffort = _agents.SelectedAgent?.ReasoningEffort
                        };
                        SelectionText = $"Codex ACP · {turn.Input.Model} · {turn.Input.Effort}";
                        OnPropertyChanged(nameof(SelectionText));
                        RefreshOptionsProjection();
                    }
                    return Task.FromResult(true);
                }
            });
            return answered
                ? new { outcome = new { outcome = "selected", optionId = implement ? "implement_plan" : "revise_plan" } }
                : cancelled;
        }
        catch (OperationCanceledException)
        {
            return cancelled;
        }
        finally
        {
            if (!answered) offer?.Expire();
            await OneWareHost.Ui(() =>
            {
                start.NotifyCanExecuteChanged();
                update.NotifyCanExecuteChanged();
                return Task.FromResult(true);
            });
        }
    }
}
