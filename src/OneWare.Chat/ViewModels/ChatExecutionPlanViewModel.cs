using CommunityToolkit.Mvvm.ComponentModel;
using OneWare.Essentials.Models;

namespace OneWare.Chat.ViewModels;

/// <summary>The current parent run's transient plan; it is not part of the saved transcript.</summary>
public sealed class ChatExecutionPlanViewModel : ObservableObject
{
    public IReadOnlyList<ChatPlanStepViewModel> Entries { get; private set; } = [];
    public Guid RunId { get; private set; }
    public ChatPlanRunState State { get; private set; }
    public bool HasEntries => Entries.Count > 0;
    public bool IsExpanded { get; set => SetProperty(ref field, value); } = true;
    public string Summary => $"{Entries.Count(x => x.IsCompleted)}/{Entries.Count} completed · " + (State switch
    {
        ChatPlanRunState.Running => "Running",
        ChatPlanRunState.Stopped => "Stopped",
        ChatPlanRunState.Failed => "Failed",
        _ => "Turn finished"
    });

    internal void Update(ChatExecutionPlanEvent update)
    {
        if (RunId != update.RunId) IsExpanded = true;
        RunId = update.RunId;
        State = update.State;
        Entries = update.Entries.Select(x => new ChatPlanStepViewModel(x, State)).ToArray();
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(Summary));
    }

    internal void Clear() => Update(new ChatExecutionPlanEvent("", Guid.Empty, []));
}

public sealed class ChatPlanStepViewModel(ChatPlanStep step, ChatPlanRunState state)
{
    public string Content => step.Content;
    public ChatPlanStepStatus Status => step.Status;
    public string Priority => step.Priority + " priority";
    public bool IsCompleted => Status == ChatPlanStepStatus.Completed;
    public bool IsInProgress => Status == ChatPlanStepStatus.InProgress && state == ChatPlanRunState.Running;
    public string StatusText => Status switch
    {
        ChatPlanStepStatus.Completed => "Completed",
        ChatPlanStepStatus.InProgress when IsInProgress => "In progress",
        ChatPlanStepStatus.InProgress => "Incomplete",
        _ => "Pending"
    };
}
