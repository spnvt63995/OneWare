using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using OneWare.Codex.Protocol;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private const string OtherAnswer = "None of the above";
    private readonly SemaphoreSlim _questions = new(1);

    private async Task<object> ElicitAsync(IAgentConnection connection, long generation,
        JsonElement parameters, CancellationToken token)
    {
        var cancelled = new { action = "cancel" };
        Prompt? turn;
        lock (_sync)
        {
            turn = _prompt;
            if (turn == null || !Current(turn) || !ReferenceEquals(connection, turn.Connection) ||
                generation != turn.Generation || Text(parameters, "sessionId") != turn.Session)
                return cancelled;
        }

        if (Text(parameters, "mode") is not (null or "form") ||
            parameters.TryGetProperty("_meta", out var meta) &&
            meta.TryGetProperty("is_mcp_tool_approval", out var approval) && approval.ValueKind == JsonValueKind.True ||
            !parameters.TryGetProperty("requestedSchema", out var schema) ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return cancelled;
        if (SelectedApprovalMode == ApprovalModeAutopilot)
        {
            lock (_sync) return Current(turn) ? AutopilotResponse(schema) : cancelled;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, turn.Cancellation.Token);
        var entered = false;
        try
        {
            await _questions.WaitAsync(cancellation.Token);
            entered = true;
            var answers = new Dictionary<string, object>();
            var required = schema.TryGetProperty("required", out var fields)
                ? fields.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                : [];
            foreach (var field in properties.EnumerateObject())
            {
                if (IsUserNote(field.Value)) continue;
                if (required.Count > 0 && !required.Contains(field.Name)) continue;
                var definition = field.Value;
                var type = Text(definition, "type");
                if (type is not ("string" or "boolean" or "integer" or "number") || IsSecretQuestion(definition))
                    return cancelled;
                var choices = new Dictionary<string, string>();
                if (definition.TryGetProperty("oneOf", out var variants))
                    foreach (var variant in variants.EnumerateArray())
                        if (Text(variant, "const") is { } value)
                            choices[Text(variant, "title") ?? value] = value;
                if (definition.TryGetProperty("enum", out var values))
                    foreach (var value in values.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String)
                            choices[value.GetString()!] = value.GetString()!;
                if (type == "boolean")
                {
                    choices["Yes"] = "true";
                    choices["No"] = "false";
                }

                // codex-acp represents a typed alternative as an explicit Other option plus a
                // linked, optional user_note property. Present them as Copilot's single question.
                JsonProperty? note = null;
                if (type == "string" && QuestionMeta(definition) is { } questionMeta &&
                    questionMeta.TryGetProperty("isOther", out var other) && other.ValueKind == JsonValueKind.True &&
                    choices.ContainsValue(OtherAnswer))
                {
                    var notes = properties.EnumerateObject().Where(x => IsUserNote(x.Value) &&
                        Text(QuestionMeta(x.Value)!.Value, "questionId") == field.Name).ToArray();
                    if (notes.Length > 1) return cancelled;
                    if (notes.Length == 1)
                    {
                        note = notes[0];
                        if (Text(note.Value.Value, "type") != "string" || IsSecretQuestion(note.Value.Value) ||
                            note.Value.Value.TryGetProperty("enum", out _) ||
                            note.Value.Value.TryGetProperty("oneOf", out _) ||
                            note.Value.Value.TryGetProperty("pattern", out _) ||
                            note.Value.Value.TryGetProperty("format", out _) || required.Contains(note.Value.Name))
                            return cancelled;
                        foreach (var key in choices.Where(x => x.Value == OtherAnswer).Select(x => x.Key).ToArray())
                            choices.Remove(key);
                    }
                }

                using var decision = new PendingDecision<object>(cancellation.Token);

                object? Parse(string? answer)
                {
                    if (answer == null) return null;
                    if (note != null && !choices.ContainsKey(answer))
                        return ValidQuestionText(note.Value.Value, answer, true)
                            ? new Dictionary<string, object> { [field.Name] = OtherAnswer, [note.Value.Name] = answer }
                            : null;
                    if (choices.Count > 0 && !choices.TryGetValue(answer, out answer)) return null;
                    if (type == "boolean") return bool.TryParse(answer, out var value) ? value : null;
                    if (type is "integer" or "number")
                    {
                        if (!double.TryParse(answer, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out var number) ||
                            !double.IsFinite(number) || type == "integer" && number != Math.Truncate(number) ||
                            definition.TryGetProperty("minimum", out var min) && number < min.GetDouble() ||
                            definition.TryGetProperty("maximum", out var max) && number > max.GetDouble()) return null;
                        return number;
                    }

                    return ValidQuestionText(definition, answer!, required.Contains(field.Name)) ? answer : null;
                }

                var command = new RelayCommand<string?>(answer =>
                {
                    lock (_sync)
                        if (Current(turn) && !cancellation.IsCancellationRequested && Parse(answer) is { } value)
                            decision.TryComplete(value);
                }, answer => Current(turn) && !cancellation.IsCancellationRequested && decision.IsPending && Parse(answer) != null);
                var question = (QuestionMeta(definition) != null ? Text(definition, "title") : null) ??
                    Text(definition, "description") ?? Text(definition, "title") ??
                    Text(parameters, "message") ?? field.Name;
                Emit(new ChatUserInputRequestEvent(question, choices.Keys.ToArray(), note != null || choices.Count == 0, command));
                try
                {
                    var answer = await decision.Task;
                    if (answer is Dictionary<string, object> alternative)
                        foreach (var (key, value) in alternative) answers[key] = value;
                    else answers[field.Name] = answer;
                }
                finally
                {
                    await OneWareHost.Ui(() =>
                    {
                        command.NotifyCanExecuteChanged();
                        return Task.FromResult(true);
                    });
                }
            }

            lock (_sync)
                return Current(turn) && !cancellation.IsCancellationRequested && required.All(x => answers.ContainsKey(x!))
                    ? new { action = "accept", content = answers } : cancelled;
        }
        catch (OperationCanceledException)
        {
            return cancelled;
        }
        finally
        {
            if (entered) _questions.Release();
        }
    }

    private static JsonElement? QuestionMeta(JsonElement definition) =>
        definition.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
        meta.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object ? codex : null;

    private static bool IsUserNote(JsonElement definition) =>
        QuestionMeta(definition) is { } meta && Text(meta, "role") == "user_note";

    private static bool IsSecretQuestion(JsonElement definition) =>
        Text(definition, "format") == "password" ||
        definition.TryGetProperty("writeOnly", out var secret) && secret.ValueKind == JsonValueKind.True ||
        QuestionMeta(definition) is { } meta && meta.TryGetProperty("isSecret", out var isSecret) &&
        isSecret.ValueKind == JsonValueKind.True;

    private static bool ValidQuestionText(JsonElement definition, string answer, bool required) =>
        (!required || !string.IsNullOrWhiteSpace(answer)) &&
        (!definition.TryGetProperty("minLength", out var min) || answer.Length >= min.GetInt32()) &&
        (!definition.TryGetProperty("maxLength", out var max) || answer.Length <= max.GetInt32());
}