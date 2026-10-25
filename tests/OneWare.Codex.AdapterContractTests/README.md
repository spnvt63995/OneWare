# Adapter contract fixtures

Independent, synthetic protocol checks for **F01–F03, F05 and F09**. This project has no
`ProjectReference` and does not load OneWare, App Server or ACP assemblies. It uses the existing
central xUnit packages and a package lock. It is registered under `/Tests/` in `OneWare.slnx`.

The fixture receives immutable normalized owner/call envelopes. Its in-memory recording delegate,
logical clock, decision handles and history store make the proposed acceptance conditions executable.
These envelopes are **not ACP wire messages**. The real adapter must still prove how connection,
session, prompt, call and replay origin map into them.

| Contract | Cases | Covered boundary |
| --- | ---: | --- |
| F01 | 4 | Both arrival orders; no invocation or approval before the pair matches |
| F02 | 30 | Provider/profile/session/connection/prompt isolation; missing and mismatched IDs, tool, server and JSON; finite matching deadline; retired scopes and conflicting notices |
| F03 | 11 | Same/different HTTP IDs while pending, executing and after success/failure; approval duplicates; 32 concurrent requests; distinct-call positive control |
| F05 | 13 | Late Allow once/chat after completion, cancellation or scope change; already-decided buttons; reused call ID in a new connection |
| F09 | 11 | Load messages/tool callbacks/permissions quarantined; late and older-load events; conflicting/unknown history; failure preservation; live traffic resumes without text-based deduplication |
| Negative controls | 5 | Disable one gate at a time and require the specific forbidden effect to be detected |

Run with the .NET 10 SDK:

```sh
dotnet test tests/OneWare.Codex.AdapterContractTests/OneWare.Codex.AdapterContractTests.csproj
```

Use `--filter FullyQualifiedName~ClassName` to select a fixture class.

## Evidence and limits

- [Acceptance contract](../../docs/CodexAdapterContract.md)
- `ContractFixture.cs`: serializable state transitions, in-memory logical-call tombstones, synthetic
  recording invocation, bounded match wait and one-shot decision ownership.
- `ReplayFixture.cs`: local history remains authoritative; unverified replay is quarantined rather
  than merged. Explicit replay origin survives completion. Identifying that origin on a real transport
  is not proved here.
- `NegativeControlTests.cs`: intentional faults exist only in this isolated fixture. They do not
  reproduce defects in the current product or establish runtime mutation coverage.

No real HTTP, CLI, AI inference, tool side effect, account, profile or history file is accessed by
the tests. F03 is an in-memory at-most-once contract for one fixture lifetime, not a durable ledger
or crash-recovery claim. F05 records whether a decision would write a chat approval; it does not store,
reuse or expire grants. Conversation approval memory is tested in `AcpApprovalMemoryTests`: an accepted
chat approval survives prompt completion and transport replacement within the same provider, whereas
new conversations and providers remain isolated. Expiring a pending button does not revoke an already
accepted approval. F09 does not
establish actual ACP load/resume compatibility or migrate any conversation. Product regressions such
as `AcpAdapterTests`, `AcpApprovalMemoryTests` and `AcpAgentRestorationTests` in
`OneWare.Codex.UnitTests` exercise the service through its actual ACP message/HTTP boundary.
They complement these models; neither suite alone establishes native UI or real-runtime compatibility.
