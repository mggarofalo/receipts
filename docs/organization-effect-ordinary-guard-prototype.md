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
