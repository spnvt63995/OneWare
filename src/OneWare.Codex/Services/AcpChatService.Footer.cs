using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    internal const string ApprovalModeKey = "Codex_AcpApprovalMode";
    public const string ApprovalModeDefault = "Default";
    public const string ApprovalModeBypass = "Bypass Approval";
    public const string ApprovalModeAutopilot = "Autopilot";
    private const string AutopilotAnswer = "The user is not available. Decide what is best and continue within the current agent's restrictions.";

    private string _approvalMode = ApprovalModeDefault;
    private Control? _footerView;
    public IReadOnlyList<string> ApprovalModes { get; } =
        [ApprovalModeDefault, ApprovalModeBypass, ApprovalModeAutopilot];
    public bool CanChangeApprovalMode => !_disposed;
    public string ApprovalModeDescription =>
        "Default asks for permission. Bypass Approval approves allowed tools. Autopilot also continues without waiting for answers. " +
        "Changes apply to subsequent requests; pending approvals and questions still need an answer.";

    public string SelectedApprovalMode
    {
        // Delegated work keeps the originating conversation's live policy, including
        // while the parent is idle. Other conversations retain their own selection.
        get => _delegation?.Owner.SelectedApprovalMode ?? Volatile.Read(ref _approvalMode);
        set
        {
            if (!CanChangeApprovalMode || !ApprovalModes.Contains(value) ||
                value == SelectedApprovalMode) return;
            Volatile.Write(ref _approvalMode, value);
            _settings?.SetSettingValue(ApprovalModeKey, value);
            OnPropertyChanged();
        }
    }

    public Control FooterUiExtension => _footerView ??= new Views.AcpChatFooterView { DataContext = this };

    private void InitializeFooter(ISettingsService? settings)
    {
        if (settings == null) return;
        if (!settings.HasSetting(ApprovalModeKey)) settings.Register(ApprovalModeKey, ApprovalModeDefault);
        RestoreApprovalMode();
        settings.WhenLoaded(RestoreApprovalMode);
    }

    private void RestoreApprovalMode()
    {
        if (_disposed || _settings == null) return;
        var value = _settings.GetSettingValue<string>(ApprovalModeKey);
        // Unknown saved values never opt a user into automatic approval.
        Volatile.Write(ref _approvalMode, ApprovalModes.Contains(value) ? value : ApprovalModeDefault);
        OnPropertyChanged(nameof(SelectedApprovalMode));
    }

    private sealed record Usage(long Input, long Output, long? Reasoning);
    private Usage? _lastUsage;
    private long _usageTurns;
    private long _totalInput;
    private long _totalOutput;
    private long? _contextUsed;
    private long? _contextLimit;

    public long? LastInputTokens => _lastUsage?.Input;
    public long? LastOutputTokens => _lastUsage?.Output;
    public long? LastReasoningTokens => _lastUsage?.Reasoning;
    public long SessionTotalTurns => _usageTurns;
    public long SessionTotalInputTokens => _totalInput;
    public long SessionTotalOutputTokens => _totalOutput;
    public long ContextCurrentTokens => _contextUsed ?? 0;
    public long ContextTokenLimit => _contextLimit ?? 1;
    public bool HasTurnUsage => _lastUsage != null;
    public bool HasSessionUsage => _usageTurns > 0;
    public bool HasReasoningUsage => _lastUsage?.Reasoning is > 0;
    public bool HasContextUsage => _contextUsed != null && _contextLimit is > 0;
    public bool HasUsageData => HasSessionUsage || HasContextUsage;

    // Called only inside the owning active prompt's notification guard. Context updates are
    // snapshots, not extra requests; they must not increase the conversation totals.
    private void ObserveContextUsage(JsonElement update)
    {
        var used = TokenCount(update, "used");
        var size = TokenCount(update, "size");
        if (used == null || size is not > 0) return;
        _contextUsed = used;
        _contextLimit = size;
        Dispatcher.UIThread.Post(RefreshFooter);
    }

    private void ObservePromptUsage(Prompt turn, JsonElement reply)
    {
        Usage recorded;
        lock (_sync)
        {
            if (!Current(turn) || turn.UsageRecorded) return;
            turn.UsageRecorded = true;
            _lastUsage = null;
            Dispatcher.UIThread.Post(RefreshFooter);
            if (!reply.TryGetProperty("usage", out var usage) ||
                TokenCount(usage, "inputTokens") is not { } input ||
                TokenCount(usage, "outputTokens") is not { } output) return;
            recorded = new Usage(input, output, TokenCount(usage, "thoughtTokens"));
            if (!RecordUsage(recorded)) return;
        }

        // Forward each accepted request directly to the owning conversation, never a child's
        // cumulative total. Nested agents therefore count once. Do not nest child/owner locks.
        if (_delegation is { } context) context.Owner.ObserveDelegatedUsage(context, recorded);
    }

    private void ObserveDelegatedUsage(Delegation context, Usage usage)
    {
        lock (_sync)
        {
            if (!context.Active || context.Cancellation.IsCancellationRequested || _disposed ||
                _session != context.OwnerSession || context.Epoch != Interlocked.Read(ref _conversationEpoch) ||
                !context.Detached && !Current(context.RootTurn)) return;
            // A detached child may finish after the parent turn. Its request updates the same
            // footer, like Copilot's assistant usage events; its context window stays separate.
            RecordUsage(usage);
        }
    }

    // Call under the receiving service's _sync lock. Last request includes child requests,
    // while the parent model label and context snapshots keep their original owners.
    private bool RecordUsage(Usage usage)
    {
        if (_usageTurns == long.MaxValue || usage.Input > long.MaxValue - _totalInput ||
            usage.Output > long.MaxValue - _totalOutput) return false;
        _lastUsage = usage;
        _usageTurns++;
        _totalInput += usage.Input;
        _totalOutput += usage.Output;
        Dispatcher.UIThread.Post(RefreshFooter);
        return true;
    }

    private static long? TokenCount(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var count) &&
        count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) && number >= 0
            ? number : null;

    private void ResetUsage()
    {
        lock (_sync)
        {
            _lastUsage = null;
            _usageTurns = _totalInput = _totalOutput = 0;
            _contextUsed = _contextLimit = null;
        }
        Dispatcher.UIThread.Post(RefreshFooter);
    }

    private void RefreshFooter()
    {
        foreach (var property in new[]
                 {
                     nameof(CanChangeApprovalMode), nameof(ApprovalModeDescription),
                     nameof(LastInputTokens), nameof(LastOutputTokens), nameof(LastReasoningTokens),
                     nameof(SessionTotalTurns), nameof(SessionTotalInputTokens), nameof(SessionTotalOutputTokens),
                     nameof(ContextCurrentTokens), nameof(ContextTokenLimit), nameof(HasTurnUsage), nameof(HasSessionUsage),
                     nameof(HasReasoningUsage), nameof(HasContextUsage), nameof(HasUsageData)
                 }) OnPropertyChanged(property);
    }

    private static object AutopilotResponse(JsonElement schema)
    {
        var properties = schema.GetProperty("properties");
        var answers = new Dictionary<string, object>();
        var required = schema.TryGetProperty("required", out var fields)
            ? fields.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
        foreach (var field in properties.EnumerateObject())
        {
            if (required.Count > 0 && !required.Contains(field.Name)) continue;
            var definition = field.Value;
            if (!AcceptsAutopilotNote(definition)) return new { action = "decline" };
            if (definition.TryGetProperty("enum", out _) || definition.TryGetProperty("oneOf", out _))
            {
                // codex-acp's explicit Other + note contract. Never pick a substantive answer
                // on behalf of the user or inject free text into a constrained enum.
                var note = properties.EnumerateObject().FirstOrDefault(x =>
                    x.Value.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("codex", out var codex) &&
                    Text(codex, "role") == "user_note" && Text(codex, "questionId") == field.Name);
                if (!definition.TryGetProperty("oneOf", out var choices) ||
                    !choices.EnumerateArray().Any(x => Text(x, "const") == "None of the above") ||
                    note.Value.ValueKind != JsonValueKind.Object || !AcceptsAutopilotNote(note.Value))
                    return new { action = "decline" };
                answers[field.Name] = "None of the above";
                answers[note.Name] = AutopilotAnswer;
            }
            else answers[field.Name] = AutopilotAnswer;
        }
        return new { action = "accept", content = answers };
    }

    private static bool AcceptsAutopilotNote(JsonElement definition) =>
        Text(definition, "type") == "string" && !definition.TryGetProperty("format", out _) &&
        !definition.TryGetProperty("pattern", out _) &&
        (!definition.TryGetProperty("writeOnly", out var secret) || secret.ValueKind != JsonValueKind.True) &&
        (!definition.TryGetProperty("_meta", out var meta) || !meta.TryGetProperty("codex", out var codex) ||
         !codex.TryGetProperty("isSecret", out var isSecret) || isSecret.ValueKind != JsonValueKind.True) &&
        (!definition.TryGetProperty("minLength", out var min) || AutopilotAnswer.Length >= min.GetInt32()) &&
        (!definition.TryGetProperty("maxLength", out var max) || AutopilotAnswer.Length <= max.GetInt32());
}
