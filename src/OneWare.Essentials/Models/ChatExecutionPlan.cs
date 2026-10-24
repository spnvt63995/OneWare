namespace OneWare.Essentials.Models;

public enum ChatPlanStepStatus { Pending, InProgress, Completed }
public enum ChatPlanPriority { Low, Medium, High }
public enum ChatPlanRunState { Running, Finished, Stopped, Failed }

public sealed record ChatPlanStep(string Content, ChatPlanStepStatus Status, ChatPlanPriority Priority);

/// <summary>
/// A complete snapshot of the current execution plan, separate from a Plan-mode approval.
/// Empty entries clear the plan. Finishing a run never implies that its steps completed.
/// </summary>
public sealed class ChatExecutionPlanEvent(string sessionId, Guid runId,
    IReadOnlyList<ChatPlanStep> entries, ChatPlanRunState state = ChatPlanRunState.Running) : ChatEvent
{
    public string SessionId { get; } = sessionId;
    public Guid RunId { get; } = runId;
    public IReadOnlyList<ChatPlanStep> Entries { get; } = entries;
    public ChatPlanRunState State { get; } = state;
}
