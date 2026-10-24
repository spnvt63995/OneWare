using System.Text.Json;
using OneWare.Essentials.Models;

namespace OneWare.Codex.Services;

internal sealed partial class AcpChatService
{
    private void PublishRuntimeTool(Prompt turn, string id, AcpRuntimeToolCall call, JsonElement update)
    {
        Emit(new ChatToolExecutionUpdateEvent(id, call.Detail ?? "", call.Output ?? ""));
        if (Text(update, "status") is not ("completed" or "failed")) return;
        turn.BackendCalls.Remove(id);
        Emit(new ChatToolExecutionCompleteEvent(id, Text(update, "status") == "completed", call.Output));
    }
}
