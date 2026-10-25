# Adapter boundary fixtures

This isolated .NET 10/xUnit project models F04, F06, F08 and F10 from
[the adapter contract](../../docs/CodexAdapterContract.md).
There are **54 contract cases and 4 fault-injection controls**.

| Contract | Cases | Executed boundary |
| --- | ---: | --- |
| F04 | 18 | Fixed-owner approval decisions and grant matching with explicit approval-key inputs |
| F06 | 14 | Invocation IDs, row ownership, client/service completion, echo suppression and in-memory edit review |
| F08 | 10 | Same-ID resume, failed retry, foreign/stale response isolation and local history preservation |
| F10 | 12 | Saved intent vs observed values; input identity, connection and prompt scope |
| Negative controls | 4 | One injected violation per modeled contract is detected by the corresponding invariant |

F04 command parsing and approval keys are tested against the product project in
[`ToolApprovalTests`](../OneWare.Codex.UnitTests/ToolApprovalTests.cs). The fixed-owner model takes those
keys as explicit inputs; it does not parse commands. Commands that cannot produce a chat scope
share one decision case here, while their distinct parsing cases remain in the product tests.

Conversation approval lifetime is tested against the real service in
[`AcpApprovalMemoryTests`](../OneWare.Codex.UnitTests/AcpApprovalMemoryTests.cs): approvals survive
transport replacement and reopening the same conversation, while new conversations and providers
remain isolated. Those tests use deterministic ACP replies and the local MCP HTTP endpoint.

Queue/Steer option timing is tested against the real service in
[`AcpLiveModelTests`](../OneWare.Codex.UnitTests/AcpLiveModelTests.cs) and
[`AcpLiveApprovalTests`](../OneWare.Codex.UnitTests/AcpLiveApprovalTests.cs): queued turns use the latest
confirmed user model and reasoning, explicit agent overrides remain pinned, steering keeps the active
turn's model, and subsequent tool requests use the live approval mode.
[`AcpFeatureTests`](../OneWare.Codex.UnitTests/AcpFeatureTests.cs) covers queued content and agent
snapshots, attachments, FIFO dispatch and steering ownership. The F10 model does not snapshot options
or attachments.

F07 result encoding is tested against the product project in
[`OneWareMcpResultTests`](../OneWare.Codex.UnitTests/OneWareMcpResultTests.cs), alongside
the real HTTP endpoint checks in `McpBoundaryTests`. This project does not compile a separate copy
of the result encoder.

F11 catalog comparison and publishing are tested against the real MCP server in
[`McpBoundaryTests`](../OneWare.Codex.UnitTests/McpBoundaryTests.cs).
[`AcpToolRefreshTests`](../OneWare.Codex.UnitTests/AcpToolRefreshTests.cs) covers handler replacement,
idle/deferred refresh, same-session resume, pending approval invalidation, retry after failure and
disposal. No separate catalog implementation is compiled here. These tests do not establish that the
ACP backend has read back and confirmed every descriptor field.

## Run

Use the .NET 10 SDK:

```sh
dotnet test tests/OneWare.Codex.AdapterBoundaryTests/OneWare.Codex.AdapterBoundaryTests.csproj
```

Use `--filter FullyQualifiedName~ClassName` to select a fixture class. The complementary ownership
and replay cases are in `OneWare.Codex.AdapterContractTests`.

## Evidence limits

No product source or project is linked into this test assembly. No
Studio process, model, CLI, MCP HTTP server, real profile or conversation is launched.
F04's fixed-owner decisions and F06/F08/F10 are bounded contract models, not the running product service.
F06 edit acceptance changes an in-memory fixture document only, not Avalonia UI or disk.
The fault controls measure oracle sensitivity, not reproduced product defects. These fixtures alone
do not establish runtime ACP compatibility.

F10's explicit/inherited intent, review-required outcomes and controlled-refresh bridge remain isolated
model conditions. They do not establish support for every inheritance or scope combination, a product
review UI, or equivalent transport recovery. Current ACP selection and queue behavior must be verified
against the product tests above. Do not change product behavior to match an older model.
