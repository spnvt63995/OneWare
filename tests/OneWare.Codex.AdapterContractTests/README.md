# Adapter contract fixtures

Independent, synthetic checks for **F05 and F09**. This project has no `ProjectReference` and does
not load OneWare, App Server or ACP assemblies. It uses the existing central xUnit packages and a
package lock. It is registered under `/Tests/` in `OneWare.slnx`.

Synthetic scope labels, decision handles and an in-memory history store make the remaining model
conditions executable. These labels are **not ACP wire messages** and do not establish how connection,
session, prompt, call or replay origin are represented on a real transport.

| Contract | Cases | Covered boundary |
| --- | ---: | --- |
| F05 | 13 | Late Allow once/chat after completion, cancellation or scope change; already-decided buttons; reused call ID in a new connection |
| F09 | 11 | Load messages/tool callbacks/permissions quarantined; late and older-load events; conflicting/unknown history; failure preservation; live traffic resumes without text-based deduplication |
| Negative controls | 2 | Stale approval and replay effects are detected when their respective model gates are disabled |

Run with the .NET 10 SDK:

```sh
dotnet test tests/OneWare.Codex.AdapterContractTests/OneWare.Codex.AdapterContractTests.csproj
```

Use `--filter FullyQualifiedName~ClassName` to select a fixture class.

## Evidence and limits

- [Acceptance boundaries](../../docs/CodexAdapterContract.md)
- `ContractEffects.cs`: records tool notices, invocations and accepted approval decisions. It does not
  simulate notification/request matching, RPC IDs, timeouts, call deduplication, JSON validation or
  stored chat grants.
- `ReplayFixture.cs`: local history remains authoritative; unverified replay is quarantined rather
  than merged. Explicit replay origin survives completion. Identifying that origin on a real transport
  is not proved here.
- `NegativeControlTests.cs`: intentional faults exist only in this isolated fixture. They do not
  reproduce defects in the current product or establish runtime mutation coverage.

No real HTTP, CLI, AI inference, tool side effect, account, profile or history file is accessed by
this project. F05 records whether a decision would write a chat approval; it does not store, reuse or
expire grants. Conversation approval memory is tested in `AcpApprovalMemoryTests`: an accepted chat
approval survives prompt completion and transport replacement within the same provider, whereas new
conversations and providers remain isolated. Expiring a pending button does not revoke an accepted
approval.

F01–F03 matching, ownership and duplicate execution are tested against the product in
`AcpAdapterTests`, `AcpToolMatchingTests`, `AcpParallelSessionTests` and `McpBoundaryTests` in
`OneWare.Codex.UnitTests`. They use deterministic ACP messages and real loopback MCP HTTP requests.
The first notice wins, an unmatched HTTP request can time out without permanently retiring the call,
and an RPC ID can be reused after its request completes. The removed model's different policies are
not compatibility requirements. The tests do not claim every former synthetic envelope combination.

F09 does not establish actual ACP load/resume compatibility or migrate any conversation. Product
regressions such as `AcpAdapterTests` and `AcpAgentRestorationTests` exercise the service through its
actual ACP message/HTTP boundary. Neither suite alone establishes native UI or real-runtime compatibility.
