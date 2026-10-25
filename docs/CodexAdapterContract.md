# Adapter contract fixtures

The initial adapter contract separates transport behavior from shared OneWare chat behavior. Its
synthetic fixtures exercise ownership, approval and replay invariants without using a real account or profile.
Passing a fixture does not establish equivalent behavior in the running ACP transport or native UI.

| Contract | Modeled boundary |
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

`tests/OneWare.Codex.AdapterContractTests` covers F01–F03, F05 and F09 with in-memory envelopes,
logical clocks and fault-injection controls. These envelopes are not ACP wire messages.

`tests/OneWare.Codex.AdapterBoundaryTests` models F04, F06, F08, F10 and F11 without compiling product
source. Its approval lifecycle takes explicit approval keys; the product parser is covered in
`tests/OneWare.Codex.UnitTests/ToolApprovalTests.cs`. F07 is also covered against the product
project in `tests/OneWare.Codex.UnitTests/OneWareMcpResultTests.cs`. In-memory
at-most-once behavior is not a durable crash-recovery ledger, and fixture edit review is not native UI.

The fixture projects target .NET 10 and use xUnit. Their READMEs describe execution and coverage.
Integration claims require separate checks of the actual transport and UI implementation.

The original models are not a complete specification of the current product. For example, the F04
model resets grants on a generic reconnect, while `AcpApprovalMemoryTests` verifies conversation-level
approval persistence. The F10 snapshot model predates the live approval and model selection covered by
`AcpLiveApprovalTests` and `AcpLiveModelTests`. F11 backend descriptor agreement remains an isolated
model check. Passing it does not prove that runtime capability.

Product regression tests use `AcpHarness` to construct the real service and `AcpPeer` to supply
deterministic ACP notifications and replies. MCP tool calls still cross the local HTTP endpoint.
These helpers are test support, not a second implementation of the adapter.
