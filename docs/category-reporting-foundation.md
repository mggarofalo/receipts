# Versioned category reporting foundation (RECEIPTS-1050)

The shared `CategoryReportReader` and `CategoryReportProjection` power
`GET /api/dashboard/spending-by-category`, including explicit historical and
current-hierarchy views. Category trends now uses the same reader with a server-side
period grouping dimension. Integration with search, exports and their UI remains required. Focused PostgreSQL query-plan evidence lives
in the external RECEIPTS-1050 review/cutoff artifacts; production-scale acceptance remains open.

## Meaning of a view

`AsClassified` selects each item's current assignment pointer and uses that
assignment's pinned taxonomy revision, including original ancestor and leaf
names. An explicit correction changes the current pointer; previous assignments
remain immutable history. The view does not mean the first assignment, or the
assignment that existed on the receipt date. An optional `knowledgeCutoff` switches
both views to the latest assignment recorded at or before that inclusive UTC cutoff,
ordered by `RecordedAt` then per-item `Sequence` descending. Higher sequence breaks
timestamp ties; a later sequence with an earlier recorded time does not outrank a
later timestamp. With no cutoff, the current pointer remains authoritative. Never
fabricate history before the migration observation boundary. `BeforeMigrationHistory`
identifies an item with no selected assignment whose observed legacy import lies
after the cutoff; it differs from `MissingAssignment` and explicit null classification.

`CurrentHierarchy` selects exactly the same assignments, captures one taxonomy
head, and resolves their stable category IDs in that snapshot. A caller may supply
`capturedCurrentRevisionId` to reuse an immutable tree across pages or report/export
reads without reading the moving head again. It is accepted only for CurrentHierarchy;
it need not still be the head. This freezes taxonomy paths, not mutable purchase facts. Moves and renames
only regroup displayed facts. Archived IDs and ancestors remain visible. Neither
view writes assignments, legacy labels, normalized product links, or payments.

Date bounds are inclusive receipt purchase dates. Callers pass their complete
authorized item query, preserving location, product, description, review-state,
price, and other existing filters. The shared reader joins live receipts and
excludes trashed receipts and items. It must run on the same context as that query.
Do not pass a paged item scope to calculate full report totals.

Historical bucket identity includes revision ID and category ID, never names.
Current buckets include the captured head and category ID. Roots can roll up
direct broad-node assignments and all descendant amounts once. A category filter
matches direct assignments plus descendants in the selected view; apply it before
the rollup. Same names under different parents remain distinct. Assigned-category
rollup retains every level of the full root-to-target path. Root rollup exposes
only the root bucket path. Intermediate rollups can extend this contract later.
Currencies remain separate; amounts and sums stay decimal without rounding.
Purchase count is item count, not quantity or receipt count. Neither item total
nor its currency is replaced with transaction/payment totals.

Null assignments, absent pointers, unresolved legacy evidence, and broken/missing
paths have distinct states. Explicit null is unclassified. A named legacy
`Uncategorized` identity is still a category. Unresolved states preserve amount
coverage in an unfiltered result, rather than disappear into an inner join. A
category filter excludes unresolved facts because ancestry cannot be proved.
The database's valid identity/revision constraints remain authoritative; broken
paths are handled defensively without an infinite walk or name-based inference.

## Query boundary and performance

The reader performs two reads for historical view, or three for current view:
optional head capture (skipped for a supplied captured revision), one grouped
purchase/selected-assignment query, then one batch snapshot query. Cutoff selection
uses server-side NOT EXISTS for a later eligible `(RecordedAt, Sequence)` assignment;
there is no per-item history read or client-side assignment selection. Counts
do not grow with item count. Migration evidence uses a server-side EXISTS in the
aggregate query, not a per-item round trip. Path traversal is iterative and cached
per revision/category pair. Full snapshots are batch-loaded only for referenced
historical revisions, or one captured current revision. No history is loaded per
purchase and no database recursion is used. Legacy migration resolution and
compatibility `unresolved_path` remain separate from deliberate null assignments.

The caller owns a repeatable-read transaction if a response promises one purchase
and assignment snapshot across queries. Otherwise concurrent explicit corrections
may affect a live report. Taxonomy snapshots themselves are immutable, so a later
head advance cannot change the captured tree. The reader has no hidden cache or
write invalidation obligation. For long-lived client query caches, taxonomy edits
invalidate current-hierarchy keys and classification corrections invalidate both
views. Dashboard spending-by-category uses this reader through the existing
dashboard service; remaining consumers are listed below.

Every result carries `View`, `Rollup`, `KnowledgeCutoff` (normalized to UTC, null for
current-pointer reads), `AssignmentSelection`, and `CurrentRevisionId` when using
CurrentHierarchy. Buckets retain path revision/category IDs, ordered path node IDs,
state and currency as stable identities. Display names are never bucket keys.

A recorded-at cutoff reconstructs classification knowledge only. Purchase date,
amount, currency, deletion state and other financial/source facts remain current at
execution; assignment history cannot reconstruct edited or deleted monetary facts.
A future immutable export must capture the amounts/results, all selection metadata,
filters and a `generatedAt` timestamp. Reusing cutoff/revision metadata alone does
not reproduce an old financial result. Use one repeatable-read transaction when a
multi-read or paged export promises consistent current facts; persist the resulting
artifact when it must remain reproducible after that transaction ends.

Required performance acceptance before cutover: PostgreSQL translation test of
left joins, conditional grouping and migration EXISTS; EXPLAIN ANALYZE with
representative purchase/revision counts; command-interceptor proof of bounded
round trips; index assessment for migration AssignmentId/Resolution lookup and
receipt date/current assignment joins. In-memory tests are not that evidence.

## Integration map and selected-view persistence

| Surface | Exact owners / required next change |
|---|---|
| API contract | `openapi/spec.yaml`: additive explicit view (`asClassified`, default; `currentHierarchy`), category UUID filter, rollup, optional recorded-at cutoff and captured current revision. Return view, captured head where relevant, bucket revision/category IDs, full path and archive flags, state, currency, decimal amount/count. Generate API DTOs and `src/client/src/generated/api.d.ts`. Reject unknown API view values with reasoned problem details. |
| Category trends | `ReportsController.GetCategoryTrends`, `GetCategoryTrendsReportQuery`/handler, `IReportService` and `ReportService.GetCategoryTrendsAsync`: replace string grouping with shared identity/path facts; retain date/granularity/top-category/Other behavior. Period must join the grouping key before server aggregation; current foundation has no temporal bucket dimension yet. Never combine multiple currencies into Other. |
| Dashboard categories | `DashboardController.GetSpendingByCategory`, matching query/handler, `IDashboardService`, `DashboardService.GetSpendingByCategoryAsync`: shared reader for category totals; define denominator per currency before top-N slicing. `GetSummaryAsync` most-used-category also needs stable bucket metadata/counts. Other summary/payment widgets retain payment-based semantics. |
| Purchase search | `SearchController`, `SearchPurchasesQuery`/validator/handler, `PurchaseSearchOptions`, `IPurchaseSearchReader`, `PurchaseSearchReader`: shared assignment/path resolver for page display and identity subtree filtering **before** count/paging. Retain current text/normalization/location/price/sort rules. Reuse predicate/path semantics without materializing all purchases or paging before category filtering. Current aggregate reader alone does not implement search filtering. |
| Other category consumers | `ReportService.GetItemDescriptionsAsync`, category mode of `GetItemCostOverTimeAsync`, `GetUncategorizedItemsAsync` and reports health counts: replace string comparisons deliberately; named Uncategorized differs from null/unresolved. Product-price history remains a separate identity. |
| Dashboard UI | `src/client/src/hooks/useDashboard.ts`, `pages/Dashboard.tsx`, `components/dashboard/SpendingByCategoryWidget.tsx`, `SummaryStats.tsx`: selector, full paths/archive indicators and currency-aware labels; add component/hook tests. |
| Reports UI | `components/reports/CategoryTrends.tsx`, `ItemCostOverTime.tsx`, `UncategorizedItems.tsx`, report hooks: use generated typed results, same view selector, identity-based picker and revision-aware chart keys. Show partial/unresolved states explicitly. |
| Purchase UI | `hooks/usePurchaseSearch.ts`, `lib/purchase-search.ts`, `lib/purchase-table.ts`, `pages/PurchaseSearch.tsx`, `components/PurchaseSearchResults.tsx`: carry view/category filter through URLs, query keys, links, table paths and tests. |
| CSV/export | `lib/export-csv.ts`, `lib/fetch-all-report-pages.ts`, report/table export callers: capture the selected view and all filters for the whole export; include view, path revision, category ID, path labels, state, currency. Do not let a concurrent view switch mix export pages. Portable taxonomy backup and YNAB mapping are separate work. |

Persist the selected view using `useReportSearchParams` and pure parsers in
`lib/report-params.ts`, following existing date/filter URL conventions. Use
`categoryView=asClassified|currentHierarchy`, default missing/invalid client values
to asClassified, preserve unrelated params, reset paging when changing the view,
and include view/rollup/category ID/cutoff and any explicitly reused captured revision in TanStack Query keys. URL navigation, reload,
shared links and export must preserve the same selected view. Dashboard, report,
search and universal-search links carry the parameter explicitly. Do not introduce
a localStorage default that silently overrides a shared URL. Current head is
response metadata, not a permanent user preference or an assignment rewrite.

## Validation scope

Focused application tests cover recorded versus current archived/moved/renamed
paths, revision-distinct historical buckets, direct/descendant rollup, parent
filtering, decimal sums, currency separation and partial states. Infrastructure
tests exercise corrected current pointers, preexisting item scopes and purchase
date selection independent of assignment recorded time, cutoff ties/backdated observations, pre-import missing history and caller-supplied captured revisions. They do not prove public
contract parity, production SQL performance, full endpoint filters, export
consistency or browser behavior. Those acceptance gates remain open.

## Category trends API cutover

`GET /api/reports/category-trends` accepts the dashboard category selection parameters.
Its existing `categories` and dense `buckets[].amounts` arrays remain parallel; additive
`series` entries identify each position by currency, revision, category, state and full path.
Top N is selected independently per currency; each remaining currency has its own Other
series. `currencyTotals` retains the full filtered denominator before slicing. Other has
`isOther=true`, `state=other`, an empty path and no fabricated taxonomy identity.
The reader groups receipt year/month/day (or quarter) together with assignment identity
and currency in SQL, then resolves the bounded grouped facts against batched snapshots.
The response captures one current revision across every period. The cutoff reconstructs
classification knowledge only; monetary facts remain current. No location filter existed
on this endpoint before this cutover.

Next consumers: dashboard summary most-used category; purchase search; item-description,
item-cost category and uncategorized reports; dashboard/report/search UI and CSV exports.
