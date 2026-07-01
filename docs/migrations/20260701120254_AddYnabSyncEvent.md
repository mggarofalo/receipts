# Migration: AddYnabSyncEvent (RECEIPTS-737)

**Migration id:** `20260701120254_AddYnabSyncEvent`
**Date:** 2026-07-01

## What it does

Creates the `ynab.YnabSyncEvents` table — an append-only log of YNAB integration attempts
(pushes and connection validations) backing the `/ynab` status page. One row is written per
attempt (success or failure). The table lands in the `ynab` bounded-context schema created by
RECEIPTS-746.

### Columns

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid (PK) | |
| `UserId` | text, null | Identity user id that triggered the attempt (provenance). |
| `OccurredAt` | timestamptz | |
| `EventType` | text | Enum `YnabSyncEventType` (`Push`, `Validate`) stored as string. |
| `ReceiptId` | uuid, null | Set for push events. |
| `TransactionId` | uuid, null | Set for push events. |
| `HttpStatus` | integer, null | YNAB response status when available. |
| `Success` | boolean | |
| `ErrorMessage` | text, null | Failure detail. |
| `RequestId` | text, null | From YNAB `X-Request-Id` when present (opportunistic). |

### Index

`IX_YnabSyncEvents_UserId_OccurredAt` on `(UserId, OccurredAt DESC)` — the acceptance-required
index. (The status/feed queries are global — YNAB is a single-PAT integration — but the index is
present as specified and still serves ordered scans.)

## Rollback

`Down` drops `ynab.YnabSyncEvents`. Data-safe in the sense that it only removes the event log;
no other table references it (no foreign keys in or out). Because the writer hook and the two
read endpoints live in application code, a rollback should be paired with reverting the
RECEIPTS-737 code change.

## Related code

- Entity/config: `Infrastructure/Entities/Core/YnabSyncEventEntity.cs`, `Infrastructure/Configurations/YnabSyncEventEntityConfiguration.cs` (excluded from audit generation in `ApplicationDbContext.CollectAuditEntries`).
- Writer/reader: `Infrastructure/Services/YnabSyncEventService.cs` (`IYnabSyncEventService`).
- Emission: `PushYnabTransactionsCommandHandler` (push events), `GetYnabConnectionStatusQueryHandler` (validate events).
- Endpoints: `GET /api/ynab/status`, `GET /api/ynab/events` (`YnabController`).

## Validation

Applied by the Testcontainers integration suite (`tests/Infrastructure.IntegrationTests`,
`Category=Integration`) which migrates a real PostgreSQL instance to HEAD: 36/37 pass (the one
failure, `PurgeTrashServiceTests`, is pre-existing and unrelated — RECEIPTS-747). CI does not run
integration tests, so this is validated locally.
