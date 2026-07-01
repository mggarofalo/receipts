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

`Down` moves every table back to `public` via the reverse `ALTER TABLE … SET SCHEMA public`. It
is symmetric and data-safe.

**Important:** the schema-qualification of the runtime raw SQL lives in application code, not in
the migration. Rolling the database back with `Down` alone is not sufficient — you must also revert
the corresponding code change, or the qualified raw SQL will reference schemas that no longer hold
those tables. Roll back code and database together.

## Validation

- Applied by the Testcontainers integration suite
  (`tests/Infrastructure.IntegrationTests`, `Category=Integration`), which migrates a real
  PostgreSQL instance to HEAD and exercises repositories, Identity, and the similarity/embedding
  raw SQL against the reorganized schema: **36 of 37 pass**. The one failure,
  `PurgeTrashServiceTests`, is pre-existing and unrelated (confirmed on `main`) — tracked in
  RECEIPTS-747.
- CI does **not** run integration tests (`dotnet test --filter "Category!=Integration"`), so this
  migration's schema move is validated by the local Testcontainers run above, not by CI.
- Test fixture `PostgresFixture` sets a `search_path` spanning all schemas (with `public` first) so
  the tests' hand-written raw SQL resolves table names regardless of which schema a table currently
  occupies — necessary because `MigrationSafetyTests` roll the database back to a pre-746 state
  (tables in `public`) and forward again within a single test.
