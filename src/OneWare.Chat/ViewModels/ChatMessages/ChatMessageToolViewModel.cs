using System.Runtime.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneWare.Essentials.Controls;

namespace OneWare.Chat.ViewModels.ChatMessages;

public class ChatMessageToolViewModel : ObservableObject, IChatMessage, IEstimatedHeightItem
{
    public ChatMessageToolViewModel(string id, string toolName)
    {
        Timestamp = DateTimeOffset.Now;
        Id = id;
        ToolName = toolName;
    }
    
    public string Id { get; init; }
    
    [DataMember]
    public string ToolName { get; }

    /// <summary>
    /// Id the AI backend assigned to this tool call, used to match the entry against the tool
    /// events of the chat service.
    /// </summary>
    public string? SourceToolCallId { get; init; }
    
    [DataMember]
    public string? ToolOutput
    {
        get;
        set => SetProperty(ref field, value);
    }
    
    public DateTimeOffset Timestamp { get; }

    public bool IsToolRunning
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Cancels this tool invocation while it is running.</summary>
    public IRelayCommand? StopCommand
    {
        get;
        set => SetProperty(ref field, value);
    }
    
    [DataMember]
    public bool IsSuccessful
    {
        get;
        set => SetProperty(ref field, value);
    }

    private string? _progressDetail;
    private string? _progressOutput;
    internal bool HasProgress { get; private set; }

    internal void UpdateProgress(string? detail, string? output)
    {
        if (!HasProgress) _progressDetail = ToolOutput;
        HasProgress = true;
        if (detail != null) _progressDetail = detail;
        if (output != null) _progressOutput = output;
        ToolOutput = string.IsNullOrWhiteSpace(_progressDetail) ? _progressOutput :
            string.IsNullOrWhiteSpace(_progressOutput) ? _progressDetail : _progressDetail + "\n" + _progressOutput;
    }

    public double EstimateHeight(double width)
    {
        const double header = 36;
        if (!IsToolRunning)
            return header;
        return header + System.Math.Min(200, ChatHeightEstimation.EstimateMarkdown(ToolOutput, width)) + 8;
    }
}
