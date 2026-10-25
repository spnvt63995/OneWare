# Codex ACP in OneWare

The desktop studio provides **Codex ACP** in AI Chat. It connects to the local Codex ACP runtime
and uses the account configured for that runtime. The provider shares OneWare's chat UI, approval
controls, tool registry, attachments and agent UI.

## Setup and daily use

1. Open AI Chat and select **Codex ACP**.
2. Use the provider's installation UI to install missing managed runtime components.
3. Sign in through the provider's account UI, then select a model and reasoning effort.
4. Attach files or images as needed and send a message. Use Stop to cancel active work.
5. Use Plan for planning, Queue for a later turn, and Steer to add input during a turn.

The footer exposes model, reasoning and approval choices together with usage/context information.
Supported choices come from the connected runtime. Saved choices apply to subsequent work; changes
during a turn follow the provider's synchronization and ownership rules.

Models with distinct default and maximum context limits also show a **Context length** picker beside
the reasoning choice. It uses Codex's model catalog, shows token lengths, and keeps the selected tier
in the application defaults and each conversation. Changing it between responses resumes the same
ACP session with `model_context_window` forwarded through `CODEX_CONFIG`; history is preserved.
Models with one size or unavailable limits keep the picker hidden. A saved long tier falls back to the
default size on such models and remains selected when returning to a model that offers both sizes.
Context limits are read using the same Codex executable and profile's `debug models` command; API-key
and external-provider sessions do not use the ChatGPT catalog. This does not change the model's real
maximum or set a separate automatic-compaction threshold.

Use the **/** button next to the model picker to search the current session's commands, read their
descriptions and enter arguments. You can also type a command directly in the chat input.
Supported commands are `/status`, `/skills`, `/mcp`, `/mcp reconnect`, `/compact`, `/review`, `/review-branch <branch>` and
`/review-commit <commit>`, when advertised by the runtime. Commands run between responses and use
the existing Stop button. Attachments remain in the draft for the next ordinary message.
Use `/mcp` to inspect server status and `/mcp reconnect` to reload MCP connections across the runtime's
open sessions. The response reports connected, starting, disabled, failed or authentication-required
servers as provided by the runtime. Servers may still be starting after a reconnect; query their status
again instead of assuming success. OneWare does not open MCP sign-in pages, and the runtime reports
when a server needs authentication that this client cannot complete.

Review commands keep workspace operations read-only. Use the existing OneWare controls for Plan,
account sign-out and conversation renaming; commands that need additional integration show a reason
in the picker instead of changing those states independently.

The runtime may not persist a new session until it receives an ordinary message. OneWare records
whether a conversation has only used `/status`, `/skills` or `/mcp` (including `reconnect`), and can
reconnect such a conversation after restarting while keeping its saved chat entry and model selection.
Restoring a conversation never reruns its commands, including reconnect. Once any other prompt is
dispatched, including a review or compaction, it requires the original session to resume. Missing
sessions with prior work or older bindings without this information show recovery guidance and are
never silently replaced.

Plugins supply tools, skills and agents through the shared host. Background delegated work allows
the parent conversation to continue. Follow-up instructions target the existing child, and completed
results are delivered to the parent conversation. Expand a child agent's tool card to see its input,
progress and result, including runtime-provided failure details. Long results are shortened for display;
image, diff and terminal references are not rendered as text results.

When the parent agent reports an execution plan, a collapsible list above the input shows pending,
in-progress and completed steps. Each update replaces the list. Stop, errors and turn completion keep
the last reported steps without marking unfinished work complete. A new run or conversation clears
the list; execution plans are not saved in chat history. This display is separate from the Plan-mode
decision to start implementation or update the proposed plan.

The managed ACP runtime still uses Codex CLI internally. Its internal App Server process is required
by that runtime; it is separate from the retired direct App Server provider.

Automatic context compaction and `/compact` share a **Context compaction** card in the parent
conversation. Updates keep the card in its original position, including after reopening the chat.
The card shows the reported state and error, with a collapsible **Retained summary** only when the
runtime supplies one. Text summaries support Markdown; other content is retained with an unsupported
content notice. Missing summary fields retain earlier content, explicit replacements overwrite it,
and clearing patches remove it. A stopped or disconnected run never implies successful compaction.
Earlier chat messages remain available, and context usage changes only when the runtime reports usage.

Live runtime notices appear above the conversation, with info, warning or error presentation and an
individual dismiss button. Unknown severities use a generic notice style. The latest 20 distinct
notices are kept; identical severity, title, description and metadata share one visible entry.
Dismissal is local, and a later occurrence can appear again. Notices are cleared when their connection,
conversation or account changes and are never stored in chat history. They do not stop a response,
change approval or model choices, or trigger retries or sign-in. Compaction updates remain in their
dedicated timeline card.

## Development entry points

- `src/OneWare.Codex/CodexModule.cs`: provider and managed-runtime registration.
- `src/OneWare.Codex/Services/AcpChatService*.cs`: sessions, prompts, approvals, tools and delegation.
- `src/OneWare.Codex/Services/AcpProfile*.cs`: profile-scoped settings and persistence.
- `src/OneWare.Chat`: shared composer, history, tools and agent UI.
- `tests/OneWare.Codex.UnitTests`: focused provider tests.
- [Adapter contract](CodexAdapterContract.md): independent synthetic transport-boundary fixtures.

Build and test using the repository's [contribution guide](../CONTRIBUTING.md).
The ACP provider tests use isolated profiles so fixture state does not affect normal conversations.
Account state, conversations and runtime files belong to the selected local profile.
