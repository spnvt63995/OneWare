using System.Text.Json;
using Microsoft.Extensions.AI;
using OneWare.Codex.Protocol;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    internal const string QuestionTool = "oneware_ask_user";
    private const string QuestionInstructions =
        "Ask user questions with mcp__oneware__oneware_ask_user. It displays OneWare's question card " +
        "and waits for a chosen or typed answer. The host handles each request using the current approval mode, " +
        "which can change during a turn. In Autopilot it returns an automatic answer instead. " +
        "Provide choices and allowFreeform=true when alternatives are welcome. " +
        "The runtime-native request_user_input_async is not connected to this ACP client's UI; use the OneWare tool instead. " +
        "Do not request passwords, credentials, or other secrets.";

    private static Dictionary<string, OneWareTool> AddQuestionTool(Dictionary<string, OneWareTool> tools)
    {
        var function = AIFunctionFactory.Create(
            (string question, string[]? choices = null, bool allowFreeform = true) => question,
            QuestionTool, "Ask the user a question in OneWare and wait for the answer. " +
                          "Show optional choices alongside a custom answer field when allowFreeform=true. " +
                          "Returns answer and wasFreeform. Ask one clear question at a time; never request secrets.");
        if (!tools.TryAdd(QuestionTool, new OneWareTool(function, true, null)))
            throw new InvalidOperationException("A plugin uses the reserved ACP tool name: " + QuestionTool);
        return tools;
    }

    private async Task<object> AskQuestionAsync(Prompt turn, JsonElement arguments, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(Text(arguments, "question")) || !Current(turn) ||
            Path.GetFullPath((await _host.CaptureContextAsync()).Directory) != turn.Directory)
            return OneWareMcpResult.Failure("The question is empty or its originating workspace changed.");
        var choices = Array.Empty<string>();
        if (arguments.TryGetProperty("choices", out var values) && values.ValueKind != JsonValueKind.Null)
        {
            if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(x =>
                    x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
                return OneWareMcpResult.Failure("Question choices must be non-empty strings.");
            choices = values.EnumerateArray().Select(x => x.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        }
        if (arguments.TryGetProperty("allowFreeform", out var freeform) &&
            freeform.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return OneWareMcpResult.Failure("allowFreeform must be a boolean.");
        var allowFreeform = choices.Length == 0 || freeform.ValueKind != JsonValueKind.False;
        var definition = new Dictionary<string, object?>
        {
            ["type"] = "string", ["title"] = Text(arguments, "question"),
            ["_meta"] = new { codex = new { isOther = allowFreeform, isSecret = false } }
        };
        var properties = new Dictionary<string, object> { ["answer"] = definition };
        if (choices.Length > 0)
        {
            definition["oneOf"] = choices.Concat(allowFreeform ? [OtherAnswer] : [])
                .Distinct(StringComparer.Ordinal).Select(x => new { @const = x, title = x }).ToArray();
            if (allowFreeform) properties["note"] = new
            {
                type = "string", _meta = new { codex = new { questionId = "answer", role = "user_note" } }
            };
        }
        var response = JsonSerializer.SerializeToElement(await ElicitAsync(turn.Connection, turn.Generation,
            JsonSerializer.SerializeToElement(new
            {
                sessionId = turn.Session, mode = "form",
                requestedSchema = new { type = "object", properties, required = new[] { "answer" } }
            }), token));
        if (!Current(turn) || Text(response, "action") != "accept" ||
            Path.GetFullPath((await _host.CaptureContextAsync()).Directory) != turn.Directory)
            return OneWareMcpResult.Failure("The question was cancelled or its originating workspace changed.");
        var content = response.GetProperty("content");
        var answer = Text(content, "note") ?? Text(content, "answer");
        return OneWareMcpResult.Success(new
        {
            answer, wasFreeform = answer != null && !choices.Contains(answer, StringComparer.Ordinal)
        });
    }
}
