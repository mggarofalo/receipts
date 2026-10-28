# Test-only effect execution closure

`EffectExecutionClosurePrototypeTests` explores structural transaction closure on
real PostgreSQL using only session-local temporary tables and functions. No trigger
attaches to application tables, no writer is registered, and no applied-effect
barrier or portable authority validator changes.

The experiment deliberately stops at **structural closure**. The current schemas
cannot supply a validated portable execution authority: the capture witness and
policy fence seal capture facts, the operation registry is inactive across writers,
and the pure fence contract explicitly leaves actor authorization and undo lineage
authentication to future work. Promoting caller-provided rows into a production
claim here would invent authority. Instead, synthetic witness, reservation, settings,
source and assignment rows are explicit assumptions, never production evidence.

A test-only PostgreSQL function derives a typed claim by joining those assumed
rows. It binds manifest/item, actor, phase, operation reservation ownership,
previous/new assignment, category, capture hash and phase hash. It checks current
source and both business revisions, taxonomy, restore generation and availability.
Apply additionally checks enabled settings and their revision, mapping revision,
manual ownership, suppression and leave-as-is protection. Assignment source is
`automatic`, distinct from the ordinary `ClassificationService` manual append path.
Undo requires the current assignment to be the original automatic apply result,
with the exact prior assignment/category. Disabling the provider or changing the
settings/mapping revision does not prevent undo; a restore-generation change or
manual replacement (including explicit clear) still blocks it.

Deferred constraint triggers symmetrically require a claim, automatic assignment,
assignment operation and terminal outcome, matching IDs, actor, lineage and hashes.
An orphan business row fails too. Current ownership must match at the transaction
that creates a claim; checking it forever would incorrectly prohibit later manual
edits. `created_tx` only scopes that structural assertion. It is not a capability.
Closure explicitly requires exactly one live owner row and an existing matching
current assignment; deleting that row cannot escape through SQL NULL comparisons.

Evidence covers caught statement errors and savepoint recovery followed by outer
commit: neither a surviving lone claim nor missing/mismatched closure rows can
commit. A control rolls back the partial claim to its savepoint, creates the full
closure and commits successfully. Separate controls exercise complete apply/undo,
manual clear and the stale/wrong binding cases.

Limitations are intentional:

- Fixture witness and phase hashes are synthetic strings. Claim hashing uses a
  separate test-only domain and PostgreSQL JSONB encoding, **not** the production
  v1 canonical codec. These tests establish no SQL/.NET codec parity.
- Fixture assignment IDs are small integers. There is one purchase and one claim
  per phase. No replay API, pagination, batch completeness or cross-session race
  protocol is proved.
- Temporary rows are mutable and session-owned. A matching reservation or hash is
  no security boundary. There is no proof of actor permissions, independent journal
  checkpoint, mapping/source-hash provenance, taxonomy path validity, complete
  capture membership, source ownership, or portable witness authenticity.
- A single synthetic live-row lock models serialization; it does not test the
  production RCPT/NORM -> Head -> Settings -> receipt/item lock order, restore
  import, trigger-depth privileges, or cross-writer operation-ID exclusion.
- These closure triggers model successful applied phases only. They are not a
  complete outcome state machine or a deployable migration. Deletes/updates used
  to damage fixture rows test consistency, not production history immutability.

Before activation, authenticated SQL derivation and immutable persistence must bind
all of the missing facts, every ordinary writer must enforce the ID namespace,
portable authority must round-trip, and actual concurrent writers/restore must be
verified together. The existing applied-effect barrier stays closed.

Run the focused experiment:

```powershell
dotnet test tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj --filter FullyQualifiedName~EffectExecutionClosurePrototypeTests
```
