# Adapter acceptance contract

The adapter contract separates transport behavior from shared OneWare chat behavior. Its synthetic
fixtures exercise ownership, approval and replay invariants without using a real account or profile.
Passing a fixture does not establish equivalent behavior in the running ACP transport or native UI.

| Contract | Required boundary |
| --- | --- |
| F01 | Match tool notifications and requests before invocation or approval, in either arrival order. |
| F02 | Reject missing, mismatched, expired or foreign provider/profile/session/connection/prompt/call ownership. |
| F03 | Invoke a logical call at most once within the fixture lifetime despite duplicate request IDs or retries. |
| F04 | Preserve shared approval conditions, one-shot decisions and chat-scoped command-prefix grants. |
| F05 | Ignore late approval decisions after their owner ends; do not execute or create a grant. |
| F06 | Keep tool rows, completion and edit review attached to the owning session and invocation. |
| F07 | Preserve supported result block order, images, errors and explicit unsupported-content behavior. |
| F08 | Resume the same session; retain history on failure and reject stale or foreign responses. |
| F09 | Quarantine replay so loading history cannot invoke tools, request approval or duplicate saved messages. |
| F10 | Preserve explicit/inherited model and reasoning intent, including Queue/Steer snapshots and scope. |
| F11 | Distinguish handler rebinding from descriptor changes; synchronize at a safe boundary with bounded retry. |
| F12 | Resolve historical aliases through current handlers or fail explicitly when a tool or argument is incompatible. |

`tests/OneWare.Codex.AdapterContractTests` covers F01–F03, F05 and F09 with in-memory envelopes,
logical clocks and fault-injection controls. These envelopes are not ACP wire messages.

`tests/OneWare.Codex.AdapterBoundaryTests` covers the other boundaries. It links the pure shared
approval parser and MCP result encoder; the other lifecycle models remain synthetic. In-memory
at-most-once behavior is not a durable crash-recovery ledger, and fixture edit review is not native UI.

The fixture projects target .NET 10 and use xUnit. Their READMEs describe execution and coverage.
Integration claims require separate checks of the actual transport and UI implementation.
