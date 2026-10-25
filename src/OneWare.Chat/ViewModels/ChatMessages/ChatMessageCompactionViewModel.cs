using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using OneWare.Essentials.Controls;
using OneWare.Essentials.Models;

namespace OneWare.Chat.ViewModels.ChatMessages;

public sealed class ChatMessageCompactionViewModel(ChatCompactionState state) : ObservableObject, IChatMessage, IEstimatedHeightItem
{
    public ChatCompactionState State { get; private set; } = state;
    public bool IsExpanded { get; set => SetProperty(ref field, value); }
    public bool IsInProgress => State.Status == "in_progress" && !State.IsInterrupted;
    public bool HasSummary => State.Summary is { Count: > 0 };
    public bool HasError => !string.IsNullOrEmpty(State.Error);
    public string? Error => State.Error;
    public string StatusText => State.Status switch
    {
        "in_progress" => State.IsInterrupted ? "Interrupted — completion not reported" : "Compacting…",
        "completed" => "Completed",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        _ => "Status: " + State.Status
    };
    public string SummaryText => string.Concat(State.Summary?.Select(SummaryTextBlock) ?? []);
    public bool HasOtherContent => State.Summary?.Any(x => SummaryTextBlock(x) == null) == true;

    private static string? SummaryTextBlock(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
        block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    internal void Apply(ChatCompactionEvent update)
    {
        State = State with
        {
            Status = update.Status ?? State.Status,
            Summary = !update.HasSummary ? State.Summary : update.AppendSummary
                ? [.. State.Summary ?? [], .. update.Summary ?? []] : update.Summary,
            Error = update.HasError ? update.Error : State.Error,
            Metadata = update.HasMetadata ? update.Metadata : State.Metadata,
            IsInterrupted = update.IsInterrupted
        };
        OnPropertyChanged(string.Empty);
    }

    public double EstimateHeight(double width) => 54 +
        (HasError ? ChatHeightEstimation.EstimateMarkdown(Error, width) : 0) +
        (HasSummary ? 30 + (IsExpanded ? Math.Min(320, ChatHeightEstimation.EstimateMarkdown(SummaryText, width)) : 0) : 0);
}
