using System.Text.Json;
using OneWare.Codex.Protocol;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private bool AllowOneWareTransport(IAgentConnection connection, long generation,
        JsonElement parameters, CancellationToken token)
    {
        // The read-only Codex sandbox asks before dispatching mutating MCP tools, including
        // implementation after Plan review. Permit only the already-correlated OneWare call
        // to reach our HTTP endpoint. This is NOT a user grant: InvokeMcpAsync still validates
        // ownership, arguments, current registration and agent restrictions, then asks the
        // original host confirmation before any execution. Native shell/file requests stay denied.
        if (token.IsCancellationRequested ||
            !parameters.TryGetProperty("_meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("is_mcp_tool_approval", out var approval) || approval.ValueKind != JsonValueKind.True ||
            !parameters.TryGetProperty("toolCall", out var call) ||
            Text(call, "kind") != "execute" || Text(call, "status") != "pending" ||
            Text(call, "toolCallId") is not { } id ||
            !parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array ||
            !options.EnumerateArray().Any(x => Text(x, "optionId") == "allow_once" && Text(x, "kind") == "allow_once"))
            return false;

        lock (_sync)
        {
            var turn = _prompt;
            if (turn == null || !Current(turn) || !ReferenceEquals(connection, turn.Connection) ||
                generation != turn.Generation || Text(parameters, "sessionId") != turn.Session ||
                !turn.Calls.TryGetValue(id, out var observed) || !observed.Task.IsCompletedSuccessfully)
                return false;
            var raw = observed.Task.Result;
            return Text(raw, "server") == OneWareMcpServer.ServerName && Text(raw, "tool") is { } name &&
                   turn.Tools.TryGetValue(name, out var tool) && turn.Allows(tool) &&
                   turn.TransportApprovals.Add(id);
        }
    }
}
