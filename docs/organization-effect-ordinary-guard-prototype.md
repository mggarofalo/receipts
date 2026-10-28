# Test-only ordinary identity guard prototype

`EffectOperationIdOrdinaryGuardPrototypeTests` attaches a session-local trigger
only to `pg_temp.ordinary_rows`. It uses the real migrated registry and claim
functions. No application table, writer, migration, capture seal, restore path or
approval flow participates. This is a narrow exclusion experiment, not activation.

Each insert or update claims all non-null operation/request UUIDs in the same
transaction as its business row. A repeated ordinary UUID remains ordinary across
rows and sessions. Updating away from an ID retains its immutable reservation.
Updating toward an effect or `legacy_blocked` ID rejects the statement, including
any other claims made by that statement. Optional null fields do not make claims.
Tests observe `pg_blocking_pids` while real PostgreSQL transactions contend in both
orders, then verify commit rejection or rollback release and business-row rollback.

The generic two-column temporary shape intentionally does not claim coverage of
every production UUID column. Before any deployment, inventory every backfill
source column in `AddInactiveEffectOperationIdRegistry`, plus every writer, direct
SQL/import and restore path. Historical apply/undo IDs can legitimately become
ordinary assignment operations; ordinary reuse across those rows must succeed.
New columns and changed IDs must participate; unchanged claims must stay
idempotent. Portable source replacement must retain destination reservations.

There is deliberately no effect execution exception. Matching an existing registry
owner, supplying a manifest/item/phase tuple, or setting session configuration
cannot authorize an assignment. `legacy_blocked` remains inert even when its old
owner matches. The current capture-binding witness is not complete execution
authority, and its seal does not consult the registry. Therefore this prototype
would reject legitimate effect-produced assignments if attached indiscriminately
to assignment tables. It must not be deployed.

The missing contract is a database-verifiable execution gate that binds an exact
validated typed capture and both clean phase reservations to the actor, item,
phase command and resulting assignment lineage, revalidating execution eligibility
and undo intent under the established lock order. A successful owner claim alone
cannot serve as this gate. Implementing such authority is outside this experiment.
Until it exists, tests for legitimate apply/undo assignment writes, wrong-owner and
wrong-phase attempts against that gate, stale execution heads, forged lineage and
blocked-owner replay cannot be truthfully supplied by this prototype.

Sorting IDs inside one row does not establish transaction-wide ordering. Multirow
statements or multiple commands can still acquire reservations in opposite orders,
and application locks may already be held before a row trigger fires. A future
transaction-entry gate must define the complete order relative to RCPT/NORM,
taxonomy Head and all reservations, with bounded retry for deadlock/serialization
failures. These tests exercise one collision per transaction and do not demonstrate
deadlock freedom or production lock compatibility.

Focused verification:

```powershell
dotnet test tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj --filter 'FullyQualifiedName~EffectOperationIdOrdinaryGuardPrototypeTests|FullyQualifiedName~EffectOperationIdRegistryTests'
```

Observed on PostgreSQL 17 via the repository's Testcontainers fixture: 13 passed,
0 failed (seven prototype cases plus six existing registry cases). The local
ignored `registry-guard-tests.log` retains the run output.

## Representative ordinary table candidates (inactive)

`EffectOperationIdOrdinaryTableCandidateTests` creates session-local `LIKE ...
INCLUDING ALL` copies of `taxonomy.HistoricalDispositionOperations` and
`category_assistance.ReviewOperations` from the migrated schema. Only these
copies receive candidate insert/update triggers. Actual columns, nullability,
check constraints and indexes are copied; PostgreSQL `LIKE` does not copy foreign
keys or source triggers. These are identity guard tests, not domain persistence
or execution eligibility tests. Review actor/operation keys and the historical
single-operation primary key retain their existing meanings. Registry exclusion
adds no cross-actor or cross-domain ordinary uniqueness rule.

Each test transaction explicitly enters a prototype gate before any domain locks
or identity claims. This gate takes one transaction-scoped advisory lock
`(1078, 831)` and records `pg_current_xact_id()` in a session-local table cleared
on commit. The candidate trigger requires that transaction's entry before
claiming `NEW.OperationId`. Entry is lost on commit and rollback; a prior entry
or session setting cannot authorize later writes. Effect contender helpers enter
the same gate before invoking the existing effect claim API. This deliberately
serializes the test transactions rather than pretending per-row UUID sorting
provides a complete transaction order. Existing registry/prototype tests continue
to exercise direct unique-index contention separately.

This temporary marker is instrumentation for cooperative test clients, not an
unforgeable execution capability or security boundary. A client that can modify
its temporary objects can forge it. It also cannot prove that the client entered
before already-held locks. No production caller invokes this gate, and the
existing claim functions do not require it. Tests use no effect assignment
exception: clean effect IDs and `legacy_blocked` IDs always reject ordinary
inserts and updates. Registry claims remain immutable after source IDs change or
source rows disappear, and business writes and reservations roll back together.

No migration installs even disabled application-table triggers. Enabling them
would change currently permitted legacy collisions and would require every
participating caller to enter before its existing domain locks. For example,
`HistoricalReclassificationService.SetDispositionAsync` already takes operation
and actor/source advisory locks before it records its disposition operation.
A row trigger cannot retroactively establish the transaction-entry order. A
future global protocol must place its gate before RCPT/NORM, actor/source,
taxonomy Head and reservation locks on every ordinary/effect/restore path, with
an explicit deadlock/serialization retry policy. The single lock in these tests
is not a production scalability choice or a proof of that protocol.

The narrow candidates therefore remain executable SQL inside the integration
test fixture. Promoting them requires a complete writer inventory, a coordinated
backfill fence, verified entry order and the database-verifiable assignment
authority described above. No deployment, activation or production safety claim
is made by passing these tests.

Focused verification of both prototypes and the real registry:

```powershell
dotnet test tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj --filter 'FullyQualifiedName~EffectOperationIdOrdinaryTableCandidateTests|FullyQualifiedName~EffectOperationIdRegistryTests|FullyQualifiedName~EffectOperationIdOrdinaryGuardPrototypeTests'
```

Observed on PostgreSQL 17 via Testcontainers: 29 passed, 0 failed (16 new
representative-table cases and 13 existing registry/prototype cases). The ignored
`ordinary-candidate-tests.log` contains the local output. The new cases include
both collision orders and commit/rollback outcomes for each table, concurrent
ordinary reuse across actor/domain, transaction-entry lifetime, rejected updates,
immutable reservations after change/delete, and multirow statement rollback
verified before the surrounding transaction commits.
