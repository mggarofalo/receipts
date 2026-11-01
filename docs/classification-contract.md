# Category and classification contract

Design for RECEIPTS-1044; implementation is owned by the following stories.
See the [ADR](decisions/2026-10-25-versioned-classification.md). All proposed APIs
below require changes to `openapi/spec.yaml`, generated clients, and tests in
their implementing PR. Existing behavior remains unchanged until that rollout.

## Identities and revisions

| Record | Required meaning |
|---|---|
| Category identity | Stable UUID; no parent, name, or provider ID in its identity. Never recycled. |
| TaxonomyRevision | UUID, monotonic sequence, previous revision, recorded-at UTC, actor, reason, operation ID. One committed current head. |
| CategoryAtRevision | Revision ID + category ID, parent category ID or null, name, definition, archived flag. Every known category occurs once per full snapshot. |
| PurchaseClassification | UUID, receipt-item ID, per-item sequence, previous assignment ID, category ID or null, taxonomy revision ID, recorded-at UTC, actor/source/reason, operation ID. |
| Current assignment projection | Receipt-item ID to latest committed assignment ID. Rebuildable; history is authoritative. |

Parent references must resolve within the same revision. Reject cycles, self
parenting, missing parents, empty names and duplicate selectable sibling names.
Name uniqueness applies to non-archived siblings (including roots). Archived
identities may share a name; historical reads always disambiguate by ID. Use exact
ordinal stored-name equality to preserve current legacy matching semantics;
do not silently trim or case-fold imported names. New names reject leading or
trailing whitespace. Equal names under different parents are valid. Do not impose
two- or three-level depth. Any operational node/depth bound must be explicit in
validation and the public contract, not a truncation in UI or traversal code.

Archive retains identity, names, and history. Archiving a node archives its entire
subtree atomically, with affected counts in the preview; restoring a child requires
active ancestors. Archived nodes are excluded from new picks but remain available
to historical reads. No hard deletion of referenced identities/revisions. Moving
a node carries its subtree; the server validates the entire resulting tree. A
move or rename creates one revision, never rewrites assignments. Identical no-op
edits retain the current revision. A new name does not merge identities.

Null category means unclassified, not a hidden sentinel category. Preserve an
existing literal `Uncategorized` category as a named category during migration;
conversion to null is an explicit later correction. Broad-node assignments are
first-class, not incomplete leaf assignments. A null assignment still pins a
revision and records provenance. Taxonomy edits cannot rewrite receipt text,
amounts, payment data, or normalized product links.

## Assignment and reporting semantics

An assignment selects a category present and selectable in its pinned revision.
Interactive assignment uses the expected current taxonomy revision; a stale head
returns 409 and requires refresh rather than silently accepting changed meaning.
Historical import can retain archived nodes through a separate validated import
path. Manual, approved mapping, accepted provider suggestion, legacy import, and
retrospective operation are distinct sources; unknown legacy origin is not proof
of automatic or manual approval. A strong Jev match written automatically has source
`jev` and a JSON reason with its probabilities and thresholds; an accepted suggestion
remains `manual`. A purchase moved by a [category restructure](restructure-plans.md) has source
`restructure` and a JSON reason naming the plan, the rule that decided it and that rule's inputs.
Provider/model/question-policy versions belong
in provenance when used, never in category identity.

The receipt date selects reporting periods. `recordedAt` orders observed
classification changes, with per-item sequence resolving equal timestamps. An
as-of knowledge cutoff chooses the last assignment recorded by that time; it must
not infer that a newly imported assignment existed on the purchase date.

| View | Assignment selected | Path used |
|---|---|---|
| As classified (default) | Latest assignment, or latest at explicit knowledge cutoff | Full path in the assignment's pinned revision |
| Current hierarchy | Same assignment selection | Same category ID in one captured current tree revision |

Responses identify the reporting mode and captured revision/cutoff. Group
historical buckets by revision and path IDs, not display text alone; show renamed
paths distinctly unless the caller explicitly requests identity aggregation.
Current-hierarchy reports retain archived IDs with their current path. Null and
unresolved migration cases have explicit separate buckets. Every purchase amount
counts once at a chosen rollup; selecting a parent includes descendant amounts
plus direct assignments to that parent, without counting descendants twice.

Example: revision T1 has `Grocery(G) > Dairy(D) > Milk(M)` and item P is assigned
M/T1 as A1. T2 renames G to Food and moves D under Chilled(C). As classified still
shows G/D/M with its T1 labels; current hierarchy shows C/D/M. If the user later
reclassifies P to Drinks(B)/T2, A2 supersedes A1 but A1 remains queryable. Undo
appends a compensating assignment referencing A1's original M/T1; it does not
pretend that M was freshly selected from T2. This privileged correction path may
restore an archived original target, and records why.

## Concurrency, permissions, and historical work

Taxonomy mutations serialize against a single current-head row and compare the
expected revision in the same transaction that inserts the full snapshot and
advances the head. Assignment writes lock the item, compare its expected latest
assignment (null for first write), validate the captured taxonomy head, append
history, and update the projection atomically. Taxonomy/head locking precedes item
locking; multi-item locks use stable ID order. Implementations must prove this
order with real PostgreSQL competing-write tests. Ordinary item edits that change
classification participate in this same application use case.

All reads require authentication. Ordinary item classification follows receipt
write authorization. Shared taxonomy mutation, historical bulk operations, and
provider configuration/checks require Admin. Permission enforcement is server-side
and applies equally to API keys; UI hiding is not authorization. Audit records
actor and operation without keys or private provider response bodies.

Use client operation UUIDs for mutating commands. Uniqueness is scoped to actor
and operation kind; store a canonical request hash and completed result. Repeating
the same operation returns its original result without duplicate history;
reusing the ID with a different body returns 409. Persist the operation record
with the write. A timeout is not proof that a command was never committed.

Historical preview captures explicit item IDs, expected item/source revisions,
assignment IDs, target taxonomy revision, filters, and protected/eligible counts.
It must include store scope when reuse is based on an exact source mapping. A
mapping key is store + exact raw description + optional item code; a missing code
is absence, never a wildcard. Fuzzy matching proposes candidates only. Existing
normalization mapping keys retain their own contract; classification does not
silently reinterpret them.

Confirmation applies that frozen set, not a new search. Default groups are 25
distinct scoped descriptions ordered by affected purchase count, with a stable
tie-breaker. Durable per-item outcomes permit restart/cancel without repeating
completed writes. Changed source/receipt location, assignment, taxonomy head,
permissions or deletion state makes an item stale; report skipped/protected
counts. Explicit inclusion is required to replace manual or legacy assignments.
Undo only appends corrections where the current assignment is still the one
written by that operation; later edits survive. UI must show partial completion
and conditional undo, not imply an all-or-nothing historical rewrite.

## Existing dependencies and migration

Inventory at base `74507f17`:

| Existing contract | Migration obligation / owner |
|---|---|
| `Domain/Core/Category.cs`, `Subcategory.cs`; `library` configurations | Suggestions, root-unique names and parent/name-unique children. Map both to category identities; retain legacy-ID crosswalks. RECEIPTS-1045/1047. |
| `Domain/Core/ReceiptItem.cs` and entity | Category/subcategory are string snapshots, not taxonomy FKs. Read stored values, including trash; retain originals and import provenance. RECEIPTS-1047. |
| `docs/category-snapshots.md` | Current rename/delete semantics do not reconstruct old suggestion paths. Do not infer old identities from today's names. RECEIPTS-1047. |
| `Domain/NormalizedDescriptions/NormalizedDescription.cs` and normalization catalog | Canonical receipt text, display labels, exact identity links and approved mappings remain separate. No mass conversion of labels into category leaves. RECEIPTS-1047/1049. |
| `Domain/Core/ItemTemplate.cs` | Retired UI still has stored defaults and optional product links; preserve compatibility import/export, never use a template link as taxonomy evidence. RECEIPTS-1047/1060. |
| `Infrastructure/Services/ReportService.cs` and purchase-search projections | Group/filter on category strings today; category trends and normalized price history are distinct. Add explicit report mode and invalidate projections after writes. RECEIPTS-1050. |
| `YnabCategoryMappingService`, `YnabSplitCalculator` | Mapping is receipt category string + budget today. Introduce ID mapping with explicit current-path nearest mapped ancestor policy; conflicts require review. Preserve sent transaction records and avoid implicit repush on a move. RECEIPTS-1047/1050/1060. |
| `BackupService`, `BackupImportService`, `PortableBackupFormat` | Version 9 includes categories, subcategories, templates, receipt label snapshots and product data. A new format must round-trip revisions, history, crosswalks, operation provenance, and trash before cutover. Never export credentials. RECEIPTS-1047/1060. |
| `openapi/spec.yaml`, generated client, aggregate receipt writes | Additive classification fields first; reconcile all writers and readers before retiring string contracts. RECEIPTS-1046/1047/1049/1060. |

Initial revision T0 means **observed at migration**, not historically valid since
the first purchase. Import assignments are recorded at migration time with
`legacy_import` provenance and original string evidence. Preserve the earliest
known history boundary and explain that pre-T0 as-of queries have no recorded
classification, rather than fabricate historical versions.

| Legacy case | Deterministic migration result |
|---|---|
| `Grocery/Dairy` and `Household/Dairy` | Two different child IDs under their exact respective parents. No global leaf-name match. |
| Category only (`Grocery`, null subcategory) | Assign the Grocery node; do not synthesize a child. |
| Empty category with a subcategory in dirty stored data | Preserve raw evidence, leave unresolved/unclassified and include in a migration exception report; no invented parent. |
| Suggestion renamed Grocery to Food, old purchase still says Grocery | Keep the old observed path as a separate legacy identity. Current names do not prove a rename relationship. Only explicit reviewed reconciliation may link meanings later. |
| Known post-cutover parent rename/move | Keep stable IDs, create Tn+1 snapshot. Old assignment pins Tn, so full ancestry remains intact. |
| Duplicate active/trashed suggestion names or UUID collision across legacy tables | Record all source IDs in a crosswalk; choose new target IDs if collision exists. Retain deleted duplicates as archived nodes in T0. If stored strings cannot identify which duplicate was meant, leave that assignment unresolved with raw evidence rather than choose the active one. Unexpected duplicate selectable siblings stop preflight for resolution before T0 commits. |
| Missing/deleted/incorrect normalized product link | Preserve or quarantine through existing backup/link validation, record exception; derive classification only from category evidence. Never create a category from the product name. |
| Label absent from suggestion lists | Create an observed legacy path from the exact strings, preserving its source and making its current availability explicit; no fuzzy reconciliation. |

Preflight produces counts for every source row, resolved assignments, unresolved
rows, duplicate paths, dirty links, trash, and money totals. Migration is
idempotent through source-ID crosswalks and a stable migration operation ID.
Ambiguous mappings stop automatic linkage and remain visible in the exception
report. Immutable original strings remain available even after compatibility
columns eventually retire.

### Legacy label strings: reads retired (RECEIPTS-1085)

`ReceiptItem.Category`/`Subcategory` are no longer read to show, search, sort,
group or report anything. Every reader uses the item's current assignment:

- Receipt list chips name the top level of each item's recorded path.
- Item search matches the recorded path (including ancestors); there is no
  string fallback, and `sortBy=category` is gone.
- Dashboard "Top category" is the top-level item type (today's tree) with the
  most purchases.
- Item code suggestions name the latest item's recorded top two levels (empty
  when it has no item type).
- Item Cost Over Time filters by `categoryId` only; the `category` and
  `categoryOnly` parameters were removed. Item descriptions group by
  description alone.
- YNAB splits key only by item envelope mapping.
- `UncategorizedItem.category` and `ItemDescriptionItem.category` are always
  empty, and `UncategorizedItem.subcategory`,
  `ItemTemplateHistoryCandidateResponse.suggestedCategory/suggestedSubcategory`
  and history rows' `SimilarItemResponse.defaultCategory/defaultSubcategory`
  are null. The fields stay so the contract does not break.

What still touches the strings, by design, until the writes are retired:

- `ReceiptItemResponse.category/subcategory` are echoed only so an edit can
  send them back unchanged; `ClassificationService.ProtectLegacyEditAsync`
  treats a changed label without a classification intent as a legacy write.
  The edit form therefore resends the stored labels whenever the item type is
  unchanged, even after the picker was reopened.
- The write path (`ClassificationService` legacy resolution, write
  fingerprints, the initial backfill) and backups.
- Eligibility guards that treat non-blank labels as an earlier human choice:
  category assistance enrolment, dispatch, read and decision stores, historical
  approved mappings and organization discovery. Moving them to the
  assignment's `LegacyCategory`/`LegacySubcategory` evidence belongs with
  retiring the writes.
- Item templates keep their own default category strings (no taxonomy field).
- The legacy category and subcategory suggestion tables (`/api/categories`,
  `/api/subcategories`) still guard deletes by counting items with a matching
  label. They go with those tables.

Chips, search and suggestions name the item type as recorded on the
assignment (its own revision), like the item rows; the dashboard's top category
uses today's tree, like the other reports.

## Spec-first endpoint/schema plan

Names are planned and must be reconciled with existing routes in the implementing
PR; do not expose placeholder endpoints. Dates use RFC 3339 UTC; IDs use UUIDs;
reasoned 4xx use the project's RFC 9457 ProblemDetails contract.

| Planned operation | Contract essentials | Owner |
|---|---|---|
| GET `/api/taxonomy` and `/api/taxonomy/revisions/{id}` | Revision metadata, full node rows, archive visibility, stable IDs | 1045 |
| POST `/api/taxonomy/changes` | Typed create/rename/move/archive/restore batch, expected revision, operation ID; atomic revision result; 409 stale head | 1045 |
| GET `/api/receipt-items/{id}/classification-history` | Cursor pagination by sequence, immutable assignments and pinned paths, legacy evidence | 1046 |
| PUT `/api/receipt-items/{id}/classification` | Nullable category ID, taxonomy revision, expected assignment ID, operation ID, source/reason; new assignment | 1046 |
| Receipt aggregate create/update | Same assignment policy in the receipt transaction, optional classification, no provider requirement | 1046/1049 |
| Category report queries | `hierarchyMode`, optional knowledge cutoff, captured current revision; metadata on every result | 1050 |
| POST classification preview/commit/undo operations | Frozen scope and revisions, protected counts, per-item outcomes, durable progress/cancel, conditional compensation | 1051/1053 |
| Provider capability/status and admin connection check | Application-owned status, separate configured/verified/feature-enabled concepts; no inference on page render | 1054/1057 |

API compatibility must reject contradictory new-ID and legacy-string assignments
with a useful 409 rather than silently preferring one. On partial updates,
omitting both leaves classification unchanged; explicit null clears it. Deep
paths cannot be faithfully encoded as two strings: legacy response labels are a
documented lossy projection only, never an authoritative round-trip. Legacy
writes on items with deeper paths must reject ambiguous changes and direct the
client to the new contract; no-op legacy echoes must not erase depth.

## Rollout, retention, and rollback

1. Ship reviewed contract and optional provider capability foundation independently.
2. Add revision/history storage, crosswalks and projections; preflight legacy data.
3. Backfill under a write fence, verify counts/history/financial invariants, then
   enable atomic dual writes through all receipt and import paths. A failed batch
   leaves the migration resumable, without marking cutover complete.
4. Round-trip the new portable backup and restore before enabling new readers.
   Imports reject unknown newer versions. Old v9 imports pass through the same
   deterministic migration, preserving the fact that older history is unknown.
5. Switch hierarchy UI, receipt picker, reports, YNAB mapping and export consumers
   behind explicit rollout controls. Then deliver historical operations.
6. Remove legacy endpoints/columns only after RECEIPTS-1060 compatibility gates
   and RECEIPTS-1061 integrated acceptance. Retained snapshots and provenance
   remain available even when old UI is gone.

Before cutover, rollback disables new readers/writers while retaining additive
tables. After new classifications/deep paths exist, deploying an old writer is
unsafe: disable writes and roll forward or restore a verified pre-cutover backup
with explicit acknowledgment of later changes. Never drop immutable history as
an automatic down migration. Do not prune snapshots referenced by assignments,
operations or backups. Measure snapshot row counts, disk growth and query cost;
storage deduplication is allowed only if the complete revision remains readable
and portable. User secrets and provider keys are never part of these backups.

## Executable acceptance ownership

This design-only story does not add tests that merely repeat the document.
Implementing stories must add the following behavior tests in their own PRs.

| Scenario | Evidence and owner |
|---|---|
| Parent moves after a leaf assignment; old path remains exact | Domain + real PostgreSQL revision/history read test, 1045/1046 |
| Concurrent edits from same head; one winner, no partial tree | PostgreSQL competing transactions, 1045 |
| Cycle, duplicate sibling, archived ancestor; broader/null choices | Domain/application validation and API rejection tests, 1045/1046 |
| Idempotent duplicate and changed-body retry | Application plus PostgreSQL operation uniqueness tests, 1046/1051 |
| Migration examples above, including trashed rows and dirty links | Versioned import fixtures and restart/rollback evidence, 1047 |
| Full old/new backup round trip with history and money unchanged | Real database/portable backup integration tests, 1047/1060 |
| As-classified vs current tree, nulls, direct-parent amounts | Report integration fixtures proving no double count, 1050 |
| Concurrent manual edit after preview and before undo | Application + PostgreSQL tests proving later work survives, 1051 |
| No Jev configuration or failed status fetch | Backend zero-outbound-call tests; frontend controls absent; manual receipt save remains possible, 1054/1049/1057 |
| Explicit category pick and exact approved reuse | Browser test: inline behavior, no per-item mandatory review, 1049 |
| Provider proposal fails, abstains or supplies unknown candidate ID | Application policy tests, no assignment written and manual fallback retained, 1057/1058 |

The separate evaluation harness (1062), live evaluation (1056), and user-provision
requirement (1055) decide whether Jev is suitable. Credentials alone do not pass
the evaluation or enable automatic classification.
