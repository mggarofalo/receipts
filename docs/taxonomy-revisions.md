# Taxonomy revision foundation

RECEIPTS-1045 adds a separate application-wide `taxonomy` schema and authenticated current/historical reads. Admin changes atomically append a complete immutable tree and advance a single locked head. Each operation persists actor, reason, time, request hash and result in the same transaction. Retries return the original revision; no-op results have zero affected nodes and retain the existing head.

The genesis revision is an empty foundation recorded on 2026-10-25. It is not an import of legacy suggestions and makes no historical claim about receipts. Existing Category/Subcategory tables, receipt string snapshots, backups, reports and UI continue their existing contracts. RECEIPTS-1047 owns legacy migration and portable backup support; do not enable taxonomy-dependent receipt writers before those gates.

POST `/api/taxonomy/changes` accepts create, rename, move, define, archive and restore changes. Names compare exactly and ordinally; active siblings must be unique. Move carries descendants; archive archives the complete subtree. Restore is explicit per node and needs active ancestors in the resulting tree. Responses include every known identity, including archived nodes, so callers can reconstruct exact historical paths. No operational depth limit is imposed.

Names contain at most 200 Unicode scalar values; definitions contain at most 4000. These bounds apply to API writes, domain validation and PostgreSQL constraints. The indexed name bound keeps even four-byte characters within PostgreSQL index entry limits. Change kind must be explicitly present in JSON.

Migration is additive. PostgreSQL rejects updates/deletes of identities, revisions, snapshot rows and completed operations. A revision records its database creation transaction; snapshot inserts require that same transaction, sealing every snapshot, including the empty genesis, at commit. The stamp is assigned by a database trigger and cannot be supplied by callers. Full snapshot creation and head advancement remain in one transaction. Automatic down migration deliberately fails to preserve history. Before cutover, disable new callers and leave the additive tables; restore a verified backup only as an explicit recovery operation. This story does not add provider dependencies, assignments, compatibility dual writes or a reader rollout.

## Category tree interface (RECEIPTS-1048)

Authenticated users can browse and search complete category paths at `/taxonomy`.
Duplicate labels remain distinct through their full paths. Archived categories are
available with **Show archived**. **Revision details** exposes the actor, reason,
time and immutable revision link; **Previous revision** opens a read-only snapshot.

Administrators can create, rename, move, archive and restore categories. Each edit
shows its affected category count before saving. Moving carries descendants;
archiving includes the entire subtree; restoring affects only the chosen node and
requires active ancestors. The editor pins its original revision and preserves an
uncertain request's operation ID for retry. A conflict requires an explicit refresh;
the draft survives while the administrator reviews the updated scope before saving.
Validation failures leave the draft editable. No receipt assignments are rewritten.

The existing `/categories` and `/subcategories` routes, query parameters and library
links remain compatible until the legacy migration/retirement gates. The tree links
to those views even when genesis is empty; it does not imply legacy receipt data has
been imported or hide that data. Taxonomy management does not invoke a provider.
