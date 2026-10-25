# Initial legacy taxonomy migration

The maintenance tool imports observed classification evidence into a new revision after
the immutable empty genesis. The observation boundary is migration time, never the
purchase date. Earlier classifications are unknown. Exact nullable label strings,
suggestion identities, deletion actors (including API-key IDs), trash and resolution
exceptions remain in the stored plan, table-qualified crosswalks and item outcomes.
Product names and template links are not classification evidence.

Run this on a verified backup with application traffic stopped during deployment.
Set `RECEIPTS_MIGRATION_CONNECTION` securely in the tool process environment, then:

```powershell
dotnet run --project tools/Receipts.TaxonomyMigration -- preflight production-2026-10-25 <admin-user-id> frozen.json
dotnet run --project tools/Receipts.TaxonomyMigration -- apply production-2026-10-25 <admin-user-id> frozen.json
```

The explicit actor must be an enabled administrator in the destination. This is an
operator maintenance command with database access, not a public API. Preflight writes
`frozen.json` and `frozen.json.report.json`; protect them as receipt data. Review the
counts, currency totals and exceptions before apply. Existing files are never
overwritten. Keep the frozen request for retries; it retains the operation UUID and
first observation boundary. Reusing the operation with changed evidence conflicts.
Apply verifies the complete current source matches the frozen request before mutation.
The service does not infer links to an existing administrator taxonomy. A target with
any revision, identity, operation or assignment beyond untouched genesis is rejected
with instructions to use a fresh restore target or reviewed reconciliation.

The first implementation admits at most **100,000 combined category, subcategory and
receipt-item source rows**, including inactive and deleted rows. Referenced receipt
parents and products are also read, with at most one distinct reference per item.
Larger datasets require a separately reviewed batched implementation; no source is
truncated. One database transaction writes the full snapshot, initial assignments,
current projection, crosswalks, outcomes and completed backfill marker. Failure rolls
back everything; retry restarts from frozen input. This is initial-migration restart,
not the resumable historical campaign workflow owned by RECEIPTS-1053.

Locks are acquired in this order: shared backup-import advisory lock, shared
normalization/admission write gate, SHARE locks on legacy suggestion/receipt/item/product
tables, then taxonomy head. Source-table SHARE locks block even uncoordinated SQL
updates and inserts while evidence is captured and committed. Head locking precedes
assignment insertion; no history triggers are disabled. Legacy transactions taking
other locks before the shared gate can still be selected as PostgreSQL deadlock
victims; stop traffic for the maintenance window and retry the unchanged frozen
request after any failed transaction. The migration itself never commits a partial
snapshot. These locks are transaction-scoped and release on cancellation or failure.

`ReadStatusAsync` reports completed **backfill**, and detects receipt items lacking a
current assignment after later imports. It does not certify backup acceptance or
authorize classification writes. Portable history round-trip evidence and reconciled
receipt writers are additional cutover gates. Keep new classification writes disabled
until those gates are implemented and verified. After cutover, old writers that cannot
preserve new classifications must not be deployed as rollback.
