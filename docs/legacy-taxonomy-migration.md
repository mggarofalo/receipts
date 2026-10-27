# Initial legacy taxonomy migration

The maintenance tool imports observed classification evidence into a new revision after
the immutable empty genesis. The observation boundary is migration time, never the
purchase date. Earlier classifications are unknown. Exact nullable label strings,
suggestion identities, deletion actors (including API-key IDs), trash and resolution
exceptions remain in the stored plan, table-qualified crosswalks and item outcomes.
Product names and template links are not classification evidence.

## Choose the migration path

An existing legacy **PostgreSQL database** needs the new EF schema first, then
explicit taxonomy preflight/apply against that upgraded database. DbMigrator adds
the schema and empty genesis; it does not perform the evidence backfill. Existing
identity/authentication state remains in that database.

An older **portable SQLite backup (v1–9)** instead imports into a fresh upgraded
database through the Admin backup API/UI. Import automatically performs the
initial taxonomy conversion in its transaction. Do not run a separate preflight/
apply over that completed import or merge the file into a populated installation.
Only untouched seed suggestions and no receipt items are admitted; exact-file
replay is a validated no-op. Portable files do not contain users or credentials.
Current v10 files carry authoritative history and require a fresh foundation or
compatible immutable-history prefix. See [backup and restore](backup-restore.md).

## In-place maintenance sequence

Keep `Classification:WritesEnabled` false (environment form
`Classification__WritesEnabled=false`) until all cutover gates below are verified.
The flag defaults to true after the classification cutover; explicitly disable it
for this maintenance sequence. It does not stop legacy writers, background services
or administrator taxonomy edits. Stop application traffic and all writers for the
maintenance window, and do not edit taxonomy before initial backfill.

1. Record the exact release/source revision and database migration level. Retain
   and verify a PostgreSQL recovery backup and separately protected deployment
   configuration/secrets. Rehearse on an isolated restored copy first. With the
   repository's Compose setup, `dotnet run scripts/backup.cs` produces a PostgreSQL
   `.sql.gz` dump; this is different from portable SQLite export.
2. Stop application writers (`docker compose stop app` for the Compose service).
   Keep PostgreSQL available. Apply the release's EF migrations without starting
   the API/workers. Use the explicit DbMigrator container command below. The
   equivalent source/SDK command, with database configuration supplied securely,
   is `dotnet run --project src/Tools/DbMigrator/DbMigrator.csproj`.
3. Run preflight on the upgraded database, review the frozen evidence/report,
   then apply the unchanged request with the same dataset and actor. Use the
   container commands below or the equivalent source/SDK commands. Taxonomy
   maintenance does not migrate schema or seed an administrator.
4. Retain the request, report, operation/revision IDs and verification evidence
   privately. Check backfill coverage including trash, financial totals, exact
   labels, unresolved outcomes, provenance and observation time. Export v10,
   restore into a fresh isolated database and verify immutable history, migration
   evidence, deletion state and business-data fidelity. Verify compatible-lineage
   restore and rejection of stale/divergent history before production cutover.
5. Resume service through the reviewed deployment procedure only after the gates
   below pass. Completed backfill alone does not authorize classification writes.

Set `RECEIPTS_MIGRATION_CONNECTION` securely in the maintenance tool process
environment. Run from the repository root, replacing the example dataset, actor
and output path with the intended database's private maintenance values:

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

## Container maintenance and restart gate

The release Dockerfile publishes API, DbMigrator, DbSeeder and the maintenance
tool at `/app/tools/TaxonomyMigration/Receipts.TaxonomyMigration.dll`. It does not
ship DbExporter. Use the exact reviewed release image for schema upgrade and
backfill; a host SDK installation is not required for these container commands.

Normal `docker-entrypoint.sh` reads secrets, runs
`dotnet tools/DbMigrator/DbMigrator.dll`, then
`dotnet tools/DbSeeder/DbSeeder.dll`, then immediately starts `API.dll` and its
background services. It provides no taxonomy-maintenance pause or taxonomy CLI
dispatch. Normal `docker compose up`/restart therefore cannot pause between schema
upgrade and evidence backfill. Keep the normal app and all other writers stopped;
override the entrypoint to run each maintenance tool directly.

For the following Linux-host examples, replace every `<...>` placeholder. Use the
production application's numeric UID/GID and the exact release image. Select the
existing Docker network that reaches the intended PostgreSQL database; neither a
network name nor a database hostname is prescribed here. Supply the database
environment variables securely in the invoking process, without putting secret
values on the command line. DbMigrator uses `POSTGRES_*`; taxonomy maintenance
uses `RECEIPTS_MIGRATION_CONNECTION`, naming the same intended database. An
entrypoint override bypasses the `/secrets` bridge and PUID/PGID adjustment, so
these variables and `--user` must be explicit.

First apply schema migrations only:

```bash
docker run --rm --user <production-uid>:<production-gid> --network <database-network> \
  --entrypoint dotnet \
  --env POSTGRES_HOST --env POSTGRES_PORT --env POSTGRES_USER \
  --env POSTGRES_PASSWORD --env POSTGRES_DB \
  <release-image> /app/tools/DbMigrator/DbMigrator.dll
```

Prepare an existing private host directory writable by that UID/GID for frozen
requests and reports. Mount it at `/artifacts`; keep the source frozen from
preflight through apply. Then run these separately, reviewing the report between
them and retaining the same dataset, actor and frozen request for retries:

```bash
docker run --rm --user <production-uid>:<production-gid> --network <database-network> \
  --entrypoint dotnet --env RECEIPTS_MIGRATION_CONNECTION \
  --mount "type=bind,source=<private-artifact-directory>,target=/artifacts" \
  <release-image> /app/tools/TaxonomyMigration/Receipts.TaxonomyMigration.dll \
  preflight <dataset> <admin-user-id> /artifacts/frozen.json

docker run --rm --user <production-uid>:<production-gid> --network <database-network> \
  --entrypoint dotnet --env RECEIPTS_MIGRATION_CONNECTION \
  --mount "type=bind,source=<private-artifact-directory>,target=/artifacts" \
  <release-image> /app/tools/TaxonomyMigration/Receipts.TaxonomyMigration.dll \
  apply <dataset> <admin-user-id> /artifacts/frozen.json
```

These one-shot commands do not invoke the normal seeder, API or its background
services. Image packaging/argument smoke checks alone do not establish successful
real-data migration. Rehearse the exact release image and maintenance sequence
against an isolated restored copy, including output permissions, database access,
schema upgrade, preflight/apply and the fidelity gates below, before production use.

The general `scripts/update.cs` health-check rollback is not sufficient evidence
for this cutover. `scripts/restore.cs` restores PostgreSQL and restarts the app in
its `finally` block; it is not a paused verification workflow. Review recovery and
restart ordering explicitly. Do not deploy an old receipt writer over new
classification history as rollback.

## Transaction and source bounds

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

## Classification-write cutover gates

`ReadStatusAsync` is a service method, not a `status` subcommand of this CLI. It
reports completed **backfill**, including receipt items in trash lacking a current
assignment. It does not certify backup acceptance or authorize classification
writes. Before enabling writes, retain reviewed evidence for:

- The isolated production-copy rehearsal and approved schema/backfill/deployment
  execution path, with verified recovery and controlled service restart.
- Portable v10 fidelity for immutable history, migration evidence, classifications,
  active data and all trash; compatible-lineage restore and stale/divergent-history
  rejection.
- Receipt entry, complete-receipt writes, edits, imports and other deployed writers
  preserving classifications and history, including cancellation/retry paths.
- Authenticated current/historical reads and operator review of unresolved legacy
  evidence. Migration records observation time, not reconstructed history.

These are deployment acceptance requirements, not a statement that production
rehearsal or cutover has passed. Keep classification writes disabled until they
are verified for the release and destination. After cutover, old writers that
cannot preserve classifications must not be deployed as rollback.
