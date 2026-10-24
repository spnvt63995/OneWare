using System.Text.Json;
using OneWare.Codex.Protocol;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private string? _sessionRecoveryNotice;

    private sealed class SessionResumeException(string message, Exception cause) : IOException(message, cause);

    private static bool IsMissingSession(Exception error, string session)
    {
        if (error is not AcpRpcException rpc) return false;
        if (rpc.Code == -32002)
            return rpc.Message == "Session not found: " + session || Text(rpc.Details, "sessionId") == session;
        // codex-acp also forwards Codex's unmaterialized-thread error as JSON-RPC internal error data.
        if (rpc.Code is not (-32603 or -32600)) return false;
        var missing = "no rollout found for thread id " + session;
        return rpc.Message.Contains(missing, StringComparison.Ordinal) ||
               (rpc.Details.ValueKind == JsonValueKind.String ? rpc.Details.GetString() :
                   Text(rpc.Details, "details") ?? Text(rpc.Details, "message"))
               ?.Contains(missing, StringComparison.Ordinal) == true;
    }

    private async Task<JsonElement> ResumeSessionAsync(string method, object servers, CancellationToken token)
    {
        var session = _session!;
        try
        {
            return await RequestSessionWithCommandsAsync(method,
                new { sessionId = session, cwd = _cwd, mcpServers = servers }, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            var missing = IsMissingSession(error, session);
            if (missing && _delegation == null && _profile.CanRecreateSession(session))
            {
                try
                {
                    var reply = await RequestSessionWithCommandsAsync("session/new",
                        new { cwd = _cwd, mcpServers = servers }, token);
                    var replacement = Text(reply, "sessionId") ?? throw new IOException("ACP did not return a session ID.");
                    token.ThrowIfCancellationRequested();
                    _profile.RebindEmptySession(_conversation ?? session, session, replacement);
                    _session = replacement;
                    _sessionRecoveryNotice = "Reconnected this command-only conversation. Your saved messages were kept.";
                    return reply;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception recoveryError)
                {
                    lock (_sync) ResetCommandsLocked();
                    throw new SessionResumeException(
                        "Could not reconnect this command-only conversation. Your saved messages were kept. " +
                        "Check the runtime and account, then reopen this chat to retry.", recoveryError);
                }
            }

            throw new SessionResumeException(missing
                ? "This ACP session is no longer available. Your saved messages were kept. Start a new chat to continue."
                : "Could not reopen this ACP conversation. Your saved messages were kept. " +
                  "Check the runtime and account, then reopen this chat to retry.", error);
        }
    }
}
