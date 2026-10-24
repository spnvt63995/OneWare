# Adapter boundary fixtures

This isolated .NET 10/xUnit project covers F04, F06–F08 and F10–F12 from
[the adapter contract](../../docs/CodexAdapterContract.md).
There are **108 contract cases and 7 fault-injection controls**.

| Contract | Cases | Executed boundary |
| --- | ---: | --- |
| F04 | 30 | Shared `ToolApproval` prefix/key parser plus a synthetic approval/grant lifecycle |
| F06 | 14 | Invocation IDs, row ownership, client/service completion, echo suppression and in-memory edit review |
| F07 | 9 | `OneWareMcpResult` encoding, content order and error semantics |
| F08 | 10 | Same-ID resume, failed retry, foreign/stale response isolation and local history preservation |
| F10 | 14 | Saved intent vs observed values; frozen Queue/Steer settings and scope checks |
| F11 | 19 | Handler rebind vs descriptor refresh, deferred synchronization, backend descriptor agreement and bounded retry |
| F12 | 12 | Current-handler legacy aliases, removed/changed arguments, archived definitions and no new dynamic aliases |
| Negative controls | 7 | One injected violation per contract is detected by the corresponding invariant |

## Run

Use the .NET 10 SDK:

```sh
dotnet test tests/OneWare.Codex.AdapterBoundaryTests/OneWare.Codex.AdapterBoundaryTests.csproj
```

Use `--filter FullyQualifiedName~ClassName` to select a fixture class. The complementary ownership
and replay cases are in `OneWare.Codex.AdapterContractTests`.

## Evidence limits

Only the two pure product files are linked into this test assembly; no product project,
Studio process, model, CLI, MCP HTTP server, real profile or conversation is launched.
F04's lifecycle and F06/F08/F10/F11/F12 are bounded contract models, not the running product service.
F06 edit acceptance changes an in-memory fixture document only, not Avalonia UI or disk.
F11's full descriptor agreement is a fixture acceptance condition; it does not validate the
running ACP backend's descriptor synchronization. The fault controls measure oracle sensitivity,
not reproduced product defects. These fixtures alone do not establish runtime ACP compatibility.
