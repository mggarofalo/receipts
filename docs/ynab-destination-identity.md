# YNAB destination identity

Receipts supports one configured YNAB personal access token at a time. Within that connection, the selected budget is part of the identity of every account mapping, category mapping and synchronization record. A local account, category or payment can therefore have independent history in more than one budget.

Changing the selected budget does not rewrite or delete prior mappings or sync records. Current mapping lists, unmapped-category results, receipt status projections, split comparisons, transaction pushes and memo synchronization all capture and query only the selected budget. A successful export to budget A does not make the same payment appear synchronized after switching to budget B; exporting to B creates a separate record and may create a separate remote transaction.

Mapping mutations carry a budget ID for API compatibility, but the application rejects a create or update when that ID is not the currently selected budget. Mapping rows cannot be moved between budgets, and update/delete routes hide rows owned by another budget. The settings page exposes an explicit destructive action for deleting previous-budget mappings; selection itself is non-destructive.

The database enforces one active sync record per local transaction, operation type and budget. Account and category mapping uniqueness likewise includes the budget. The `20260910081448_ScopeYnabDestinationIdentity` migration replaces the former destination-agnostic indexes without reassigning existing rows, so their recorded budget ownership is preserved.

Budget IDs are UUIDs. Selection and mapping commands canonicalize accepted UUID spellings to lowercase `D` form before comparison or persistence, and the migration normalizes UUID-shaped historical values in the selected-budget, mapping and sync-record tables. Backup restore applies the same normalization while retaining arbitrary legacy non-UUID values. This prevents uppercase, braced or compact spellings of the same remote budget from becoming separate local destinations.

Account merges reconcile mappings independently per budget, so mappings for different destinations survive together on the merged account. A single conflicting budget can still be resolved by choosing its winning account. A merge with conflicts in multiple budgets fails closed without mutation because the current request contract cannot express a different winner for each budget; remove obsolete mappings until only one budget conflicts, then retry. Merge previews report the number of destination-scoped mappings that will move.

Each operation captures the selected budget before loading mappings or sync state and carries that same ID to YNAB. If another request changes the singleton selection concurrently, the in-flight operation continues against its captured destination; a later operation observes the new selection.

After multiple-budget bindings have been created, the predecessor schema cannot represent the data. The migration's downgrade checks for those collisions before changing any index and refuses with an actionable error. An operator must explicitly remove extra destination bindings before retrying a downgrade; the migration never chooses which history to discard.
