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

Plugins supply tools, skills and agents through the shared host. Background delegated work allows
the parent conversation to continue. Follow-up instructions target the existing child, and completed
results are delivered to the parent conversation.

The managed ACP runtime still uses Codex CLI internally. Its internal App Server process is required
by that runtime; it is separate from the retired direct App Server provider.

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
