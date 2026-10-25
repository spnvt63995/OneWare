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
| F10 | Separate saved model/reasoning intent from observed values and check queued/steered input ownership. |
| F11 | Distinguish handler rebinding from descriptor changes; synchronize at a safe boundary with bounded retry. |

`tests/OneWare.Codex.AdapterContractTests` covers F01–F03, F05 and F09 with in-memory envelopes,
logical clocks and fault-injection controls. These envelopes are not ACP wire messages.

`tests/OneWare.Codex.AdapterBoundaryTests` models F04, F06, F08, F10 and F11 without compiling product
source. Its fixed-owner approval decisions take explicit approval keys; the product parser is covered in
`tests/OneWare.Codex.UnitTests/ToolApprovalTests.cs`. Conversation approval persistence and isolation are
covered against the real service in `tests/OneWare.Codex.UnitTests/AcpApprovalMemoryTests.cs`.
F07 is also covered against the product
project in `tests/OneWare.Codex.UnitTests/OneWareMcpResultTests.cs`. In-memory
at-most-once behavior is not a durable crash-recovery ledger, and fixture edit review is not native UI.

The fixture projects target .NET 10 and use xUnit. Their READMEs describe execution and coverage.
Integration claims require separate checks of the actual transport and UI implementation.

The original models are not a complete specification of the current product. F10 models saved intent
and input ownership independently; it does not freeze options or attachments. Product tests
`AcpLiveApprovalTests`, `AcpLiveModelTests` and `AcpFeatureTests` cover live approval, next-message model
selection, queued content/agent snapshots and steering ownership. The model's explicit/inherited
combinations, review-required outcomes and controlled-refresh bridge do not establish equivalent
product support. F11 backend descriptor agreement also remains an isolated model check. Passing it
does not prove that runtime capability.

Product regression tests use `AcpHarness` to construct the real service and `AcpPeer` to supply
deterministic ACP notifications and replies. MCP tool calls still cross the local HTTP endpoint.
These helpers are test support, not a second implementation of the adapter.
