# Migration: OrganizeTablesIntoSchemas (RECEIPTS-746)

**Migration id:** `20260701091936_OrganizeTablesIntoSchemas`
**Date:** 2026-07-01

## What it does

Moves every domain table out of PostgreSQL's default `public` schema into a bounded-context
schema, so table ownership is legible at the database level. No table is renamed and no data
is touched — each move is an `ALTER TABLE … SET SCHEMA`, which carries the table's columns,
indexes, check constraints, and foreign keys (including cross-schema FKs) with it.

### Premise note

The originating issue was written against a SQL Server mental model (`dbo`,
`ALTER SCHEMA … TRANSFER`). This application runs on **PostgreSQL** (Npgsql + pgvector); there
is no `dbo`, and the default schema is `public`. The plan and this migration use PostgreSQL
semantics. Several tables the issue named do not exist (e.g. `ReceiptImages`, `YnabSettings`,
`SecurityEvents`) and several real ones it omitted are included below.

## Schema map

| Schema | Tables |
|---|---|
| `identity` | AspNetUsers, AspNetRoles, AspNetUserRoles, AspNetUserClaims, AspNetUserLogins, AspNetUserTokens, AspNetRoleClaims, ApiKeys |
| `receipts` | Receipts, ReceiptItems, Adjustments, Transactions |
| `library` | Accounts, Cards, Categories, Subcategories, ItemTemplates |
| `ynab` | YnabSelectedBudgets, YnabAccountMappings, YnabCategoryMappings, YnabServerKnowledge, YnabSyncRecords |
| `audit` | AuditLogs, AuthAuditLogs |
| `matching` | ItemEmbeddings, NormalizedDescriptions, NormalizedDescriptionSettings, DistinctDescriptions, ItemSimilarityEdges |
| `public` (unchanged) | `__EFMigrationsHistory`, `__SeedHistory` |

Notes:
- The issue's proposed `backup` schema is dropped — backups are separate SQLite files
  (`BackupService`), not PostgreSQL tables.
- `__EFMigrationsHistory` stays in `public`: no global `HasDefaultSchema` is set, so EF keeps
  finding its history table where it already is. `__SeedHistory` stays alongside it as infra meta.

## Code changes that accompany the migration

- **Entity configurations** (`src/Infrastructure/Configurations/*`): each `IEntityTypeConfiguration`
  now calls `ToTable(name, schema)`. `SeedHistoryEntryConfiguration` is intentionally left in
  `public`.
- **Identity tables** (`ApplicationDbContext.OnModelCreating`): the seven `AspNet*` tables have no
  configuration class (they are mapped by `base.OnModelCreating`), so their schema is set with
  explicit `ToTable(name, "identity")` overrides after the base call.
- **Runtime raw SQL** (`ApplicationDbContext`, `ItemSimilarityEdgeRefresher`,
  `ItemTemplateSimilarityService`, `NormalizedDescriptionService`): hand-written SQL relies on
  `search_path`, so every moved-table reference is now schema-qualified (e.g.
  `"matching"."DistinctDescriptions"`, `"receipts"."ReceiptItems"`). pg_trgm / pgvector operators
  and functions (`%`, `<=>`, `set_limit()`) stay unqualified — they live in `public`, which
  remains in the default `search_path`. EF-generated queries are always schema-qualified, so they
  needed no change.
- **Historical migrations are left untouched.** They run (on a fresh database) before this
  migration, while the tables are still in `public`, so their unqualified raw SQL is correct.

## Rollback notes

`Down` is symmetric and data-safe. For every table it emits the reverse
`ALTER TABLE "<schema>"."<table>" SET SCHEMA public`, then drops the six now-empty
bounded-context schemas (`DROP SCHEMA <name>`), returning the database to its exact
pre-migration state. `Up`'s `EnsureSchema` keeps re-application idempotent.

> **RECEIPTS-749:** the symmetric teardown was completed here. As originally scaffolded, the
> `Down` `RenameTable` operations specified no `newSchema`, so they generated no SQL — the tables
> were never moved back to `public` and the schemas were left in place. `Down` now sets
> `newSchema: "public"` on each move and drops the emptied schemas. `MigrationSafetyTests` rolls
> the database back past this migration, so it verifies the teardown succeeds; its
> null-`CardId` assertion queries the row with unqualified raw SQL because, mid-rollback, the
> tables legitimately live in `public` rather than their `receipts`/etc. schemas.

**Important:** the schema-qualification of the runtime raw SQL lives in application code, not in
the migration. Rolling the database back with `Down` alone is not sufficient — you must also revert
the corresponding code change, or the qualified raw SQL will reference schemas that no longer hold
those tables. Roll back code and database together.

## Validation

- Applied by the Testcontainers integration suite
  (`tests/Infrastructure.IntegrationTests`, `Category=Integration`), which migrates a real
  PostgreSQL instance to HEAD and exercises repositories, Identity, and the similarity/embedding
  raw SQL against the reorganized schema: **37 of 37 pass** (including `MigrationSafetyTests`,
  which exercises the symmetric `Down`). The previously-failing `PurgeTrashServiceTests` was a
  pre-existing, unrelated FK-seed bug, fixed under RECEIPTS-747.
- CI does **not** run integration tests (`dotnet test --filter "Category!=Integration"`), so this
  migration's schema move is validated by the local Testcontainers run above, not by CI.
- Test fixture `PostgresFixture` sets a `search_path` spanning all schemas (with `public` first) so
  the tests' hand-written raw SQL resolves table names regardless of which schema a table currently
  occupies — necessary because `MigrationSafetyTests` roll the database back to a pre-746 state
  (tables in `public`) and forward again within a single test.
