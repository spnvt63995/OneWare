# Adapter contract fixtures

The initial adapter contract separates transport behavior from shared OneWare chat behavior. Its
synthetic fixtures exercise ownership, approval and replay invariants without using a real account or profile.
Passing a fixture does not establish equivalent behavior in the running ACP transport or native UI.

| Contract | Boundary under test |
| --- | --- |
| F01 | Match tool notifications and requests before invocation or approval, in either arrival order. |
| F02 | Check the active connection/prompt, session, call ID, tool and arguments at the product boundary. |
| F03 | Claim each logical call once in its prompt; reserve an HTTP RPC ID only while that request is active. |
| F04 | Preserve shared approval conditions, one-shot decisions and chat-scoped command-prefix grants. |
| F05 | Ignore late approval decisions after their owner ends; do not execute or create a grant. |
| F06 | Keep tool rows, completion and edit review attached to the owning session and invocation. |
| F07 | Preserve supported result block order, images, errors and explicit unsupported-content behavior. |
| F08 | Resume the same session; retain history on failure and reject stale or foreign responses. |
| F09 | Quarantine replay so loading history cannot invoke tools, request approval or duplicate saved messages. |
| F10 | Separate saved model/reasoning intent from observed values and check queued/steered input ownership. |
| F11 | Rebind unchanged tool handlers and refresh local descriptors at a safe prompt boundary. |

`tests/OneWare.Codex.AdapterContractTests` models F05 and F09 with synthetic scope labels,
decision handles, effect recording and fault-injection controls. These labels are not ACP wire messages.

F01–F03 are covered at the product boundary by `AcpAdapterTests`, `AcpToolMatchingTests`,
`AcpParallelSessionTests` and `McpBoundaryTests`. Both arrival orders wait for a matching pair before
approval or invocation. JSON object property order does not affect matching; array order does.
The adapter checks its connection/generation and active prompt plus the session and call IDs carried
by MCP; it does not receive the old model's provider/profile/connection/prompt envelope on the wire.
Existing cancellation, conversation isolation and profile-binding tests cover their respective owners.

The first tool-call notice wins; a later conflicting notice does not replace a pending approval.
A request that times out waiting for its notice fails that HTTP request without permanently retiring
the logical call. A later matched retry can run. Concurrent callbacks and retries during invocation or
after success/failure do not invoke that logical call again. The MCP server releases its RPC ID when
the HTTP invocation finishes, including failure. There is no independent matching clock, permanent
RPC ledger, recursive duplicate-JSON-key policy or durable crash-recovery guarantee in the fixtures.
These product tests do not exhaust every malformed envelope or every connection/cancellation race.

F05's model records accepted decision effects only; it does not store or expire conversation grants.
An expired pending decision and an already accepted chat approval have different lifetimes.

`tests/OneWare.Codex.AdapterBoundaryTests` models F04, F08 and F10 without compiling product
source. Its fixed-owner approval decisions take explicit approval keys; the product parser is covered in
`tests/OneWare.Codex.UnitTests/ToolApprovalTests.cs`. Conversation approval persistence and isolation are
covered against the real service in `tests/OneWare.Codex.UnitTests/AcpApprovalMemoryTests.cs`.
F07 is also covered against the product
project in `tests/OneWare.Codex.UnitTests/OneWareMcpResultTests.cs`.

F06 host invocation IDs and row progress/completion are covered by product tests in `AcpAdapterTests`
and `AcpToolDetailsTests`, including completion in the original chat while another is selected and
separate handling of host backend notifications and runtime tools. There is no independent presentation
or edit-review model. Shared `AiFileEditService` writes edits immediately; Accept closes review and Undo
restores the original when allowed. The display tests do not establish end-to-end editor review coverage
or a universal at-most-once UI completion guarantee.

The fixture projects target .NET 10 and use xUnit. Their READMEs describe execution and coverage.
Integration claims require separate checks of the actual transport and UI implementation.

The original models are not a complete specification of the current product. F10 models saved intent
and input ownership independently; it does not freeze options or attachments. Product tests
`AcpLiveApprovalTests`, `AcpLiveModelTests` and `AcpFeatureTests` cover live approval, next-message model
selection, queued content/agent snapshots and steering ownership. The model's explicit/inherited
combinations, review-required outcomes and controlled-refresh bridge do not establish equivalent
product support.

F11 is covered against the product by `McpBoundaryTests` and `AcpToolRefreshTests`. Local catalog
comparison, publication and session refresh do not confirm a backend readback of every descriptor.
The product's bounded local registry capture is distinct from backend retries. Its current catalog
signature preserves JSON schema property order, and changes during pending approval invalidate the
request. These behaviors must not be replaced by assumptions from an independent catalog model.

Product regression tests use `AcpHarness` to construct the real service and `AcpPeer` to supply
deterministic ACP notifications and replies. MCP tool calls still cross the local HTTP endpoint.
These helpers are test support, not a second implementation of the adapter.
