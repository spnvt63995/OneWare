using System.Text.Json;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private void ObserveExecutionPlan(Prompt turn, JsonElement update)
    {
        if (_delegation != null || !update.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array) return;

        var steps = new List<ChatPlanStep>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, "content") is not { } content || string.IsNullOrWhiteSpace(content)) return;
            ChatPlanStepStatus? status = Text(entry, "status") switch
            {
                "pending" => ChatPlanStepStatus.Pending,
                "in_progress" => ChatPlanStepStatus.InProgress,
                "completed" => ChatPlanStepStatus.Completed,
                _ => null
            };
            ChatPlanPriority? priority = Text(entry, "priority") switch
            {
                "low" => ChatPlanPriority.Low,
                "medium" => ChatPlanPriority.Medium,
                "high" => ChatPlanPriority.High,
                _ => null
            };
            // A malformed snapshot must not erase the last valid plan.
            if (status == null || priority == null) return;
            steps.Add(new ChatPlanStep(content, status.Value, priority.Value));
        }

        turn.PlanEntries = steps.ToArray();
        PublishExecutionPlan(turn);
    }

    private void PublishExecutionPlan(Prompt turn, ChatPlanRunState state = ChatPlanRunState.Running)
    {
        if (_delegation == null)
            Emit(new ChatExecutionPlanEvent(_conversation ?? turn.Session, turn.LocalId, turn.PlanEntries, state));
    }
}
