# Adapter boundary fixtures

This isolated .NET 10/xUnit project models F04, F06, F08, F10 and F11 from
[the adapter contract](../../docs/CodexAdapterContract.md).
There are **75 contract cases and 5 fault-injection controls**.

| Contract | Cases | Executed boundary |
| --- | ---: | --- |
| F04 | 18 | Fixed-owner approval decisions and grant matching with explicit approval-key inputs |
| F06 | 14 | Invocation IDs, row ownership, client/service completion, echo suppression and in-memory edit review |
| F08 | 10 | Same-ID resume, failed retry, foreign/stale response isolation and local history preservation |
| F10 | 14 | Saved intent vs observed values; frozen Queue/Steer settings and scope checks |
| F11 | 19 | Handler rebind vs descriptor refresh, deferred synchronization, backend descriptor agreement and bounded retry |
| Negative controls | 5 | One injected violation per modeled contract is detected by the corresponding invariant |

F04 command parsing and approval keys are tested against the product project in
[`ToolApprovalTests`](../OneWare.Codex.UnitTests/ToolApprovalTests.cs). The fixed-owner model takes those
keys as explicit inputs; it does not parse commands. Commands that cannot produce a chat scope
share one decision case here, while their distinct parsing cases remain in the product tests.

Conversation approval lifetime is tested against the real service in
[`AcpApprovalMemoryTests`](../OneWare.Codex.UnitTests/AcpApprovalMemoryTests.cs): approvals survive
transport replacement and reopening the same conversation, while new conversations and providers
remain isolated. Those tests use deterministic ACP replies and the local MCP HTTP endpoint.

F07 result encoding is tested against the product project in
[`OneWareMcpResultTests`](../OneWare.Codex.UnitTests/OneWareMcpResultTests.cs), alongside
the real HTTP endpoint checks in `McpBoundaryTests`. This project does not compile a separate copy
of the result encoder.

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
F04's fixed-owner decisions and F06/F08/F10/F11 are bounded contract models, not the running product service.
F06 edit acceptance changes an in-memory fixture document only, not Avalonia UI or disk.
F11's full descriptor agreement is a fixture acceptance condition; it does not validate the
running ACP backend's descriptor synchronization. The fault controls measure oracle sensitivity,
not reproduced product defects. These fixtures alone do not establish runtime ACP compatibility.

F10 retains its original frozen submission options assumption. The current product supports live
policy/model selection, covered by `AcpLiveApprovalTests` and `AcpLiveModelTests`.
Do not change product behavior to match an older model.
