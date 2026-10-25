# Adapter approval decision fixtures

Independent, synthetic checks for **F05**. This project has no `ProjectReference` and does not load
OneWare, App Server or ACP assemblies. It uses the existing central xUnit packages and a package lock.
It is registered under `/Tests/` in `OneWare.slnx`.

| Contract | Cases | Covered boundary |
| --- | ---: | --- |
| F05 | 13 | Late Allow once/chat after completion, cancellation or scope change; already-decided buttons; reused call ID in a new connection |
| Negative control | 1 | A stale decision produces a forbidden approval effect when the model's guard is disabled |

`ApprovalFixture.cs` models pending decision ownership and records accepted decisions. Its scope
labels are not ACP wire fields. It does not implement matching, RPC IDs, timeouts, JSON validation,
history replay, tool execution or stored chat grants.

Run with the .NET 10 SDK:

```sh
dotnet test tests/OneWare.Codex.AdapterContractTests/OneWare.Codex.AdapterContractTests.csproj
```

Use `--filter FullyQualifiedName~ClassName` to select a fixture class.

## Evidence and limits

No real HTTP, CLI, AI inference, tool side effect, account, profile or history file is accessed by
this project. The negative control measures this model's sensitivity, not a reproduced product defect.

F05 records whether a decision would write a chat approval; it does not store, reuse or expire grants.
Product `AcpApprovalMemoryTests` covers conversation approval lifetime: accepted approvals survive
prompt completion and transport replacement within the same provider, while other conversations and
providers remain isolated. Expiring a pending button does not revoke an accepted approval.

F01–F03 and F08–F10 are described with their actual product tests in
[the adapter boundaries](../../docs/CodexAdapterContract.md). Ordinary replay is tested through
`AcpSessionBoundaryTests` and `AcpAdapterTests`, including saved history and subsequent live messages.
Normal ID-addressed compaction replay is covered separately by `AcpCompactionTests`.
There is no independent load-origin tag, replay-message-ID validator or history merge model here.
Neither suite alone establishes native UI or real-runtime compatibility.
