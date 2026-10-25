# Adapter boundary fixtures

This isolated .NET 10/xUnit project models F04 from
[the adapter contract](../../docs/CodexAdapterContract.md).
There are **18 contract cases and 1 fault-injection control**.

| Contract | Cases | Executed boundary |
| --- | ---: | --- |
| F04 | 18 | Fixed-owner approval decisions and grant matching with explicit approval-key inputs |
| Negative control | 1 | Accepting only one compound-command prefix is detected by the grant invariant |

F04 command parsing and approval keys are tested against the product project in
[`ToolApprovalTests`](../OneWare.Codex.UnitTests/ToolApprovalTests.cs). The fixed-owner model takes those
keys as explicit inputs; it does not parse commands. Commands that cannot produce a chat scope
share one decision case here, while their distinct parsing cases remain in the product tests.

Conversation approval lifetime is tested against the real service in
[`AcpApprovalMemoryTests`](../OneWare.Codex.UnitTests/AcpApprovalMemoryTests.cs): approvals survive
transport replacement and reopening the same conversation, while new conversations and providers
remain isolated. Those tests use deterministic ACP replies and the local MCP HTTP endpoint.

F06 invocation and display behavior is tested against the product in
[`AcpAdapterTests`](../OneWare.Codex.UnitTests/AcpAdapterTests.cs) and
[`AcpToolDetailsTests`](../OneWare.Codex.UnitTests/AcpToolDetailsTests.cs). Real host invocation IDs,
progress and completion stay with their conversation when another chat is selected; host backend
notifications do not take over the native function row. Runtime-tool completion and late updates
have separate product checks. These tests do not impose a universal completion counter or synthetic
connection/prompt keys on the shared UI.

Edit review uses the shared `AiFileEditService`: editing writes the file immediately, Accept closes
the review, and Undo restores the original when allowed. There is no independent edit-review model
here. The display tests above do not establish end-to-end Accept/Undo or native editor coverage.

Queue/Steer option timing is tested against the real service in
[`AcpLiveModelTests`](../OneWare.Codex.UnitTests/AcpLiveModelTests.cs) and
[`AcpLiveApprovalTests`](../OneWare.Codex.UnitTests/AcpLiveApprovalTests.cs): queued turns use the latest
confirmed user model and reasoning, explicit agent overrides remain pinned, steering keeps the active
turn's model, and subsequent tool requests use the live approval mode.
[`AcpFeatureTests`](../OneWare.Codex.UnitTests/AcpFeatureTests.cs) covers queued content and agent
snapshots, attachments, FIFO dispatch and steering ownership.
`AcpSessionBoundaryTests` checks workspace changes through the real service: steering is rejected,
waiting input is not dispatched, and its attachments return to the draft for explicit resubmission.
`AcpOptionsTests` covers persisted user/default/conversation selections; there is no independent
explicit/inherited intent payload, review-required UI or controlled-connection bridge model.

F08/F09 restoration and history are covered in `AcpSessionBoundaryTests`, `AcpAdapterTests`,
`AcpCommandRecoveryTests` and `AcpAgentRestorationTests`. Ordinary conversations and children
retain their runtime ID; only eligible command-only conversations may rebind after a confirmed
missing-session response. Original workspace/profile bindings and saved messages are preserved.
The product serializes session loading rather than comparing synthetic resume tickets or reply cwd.
`AcpCompactionTests` covers the normal ID-addressed replay exception.

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

Use `--filter FullyQualifiedName~ClassName` to select a fixture class. Complementary pending-decision
models are in `OneWare.Codex.AdapterContractTests`; transport regressions are in `OneWare.Codex.UnitTests`.

## Evidence limits

No product source or project is linked into this test assembly. No
Studio process, model, CLI, MCP HTTP server, real profile or conversation is launched.
F04's fixed-owner decisions are bounded contract models, not the running product service.
The fault controls measure oracle sensitivity, not reproduced product defects. These fixtures alone
do not establish runtime ACP compatibility.

The product tests above do not establish every malformed reply, concurrency race or inheritance
combination from the original models. Do not change product behavior to match an older model.
