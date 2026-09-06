# Category suggestions and historical receipt labels

Receipt items store category and subcategory names as historical snapshots. Suggestions help users enter those names; they do not own historical rows. Renaming or deleting a suggestion does not rewrite receipt items.

Deleting a category is blocked by any receipt item whose category snapshot equals the category's current name. Deleting a subcategory checks both its parent's current name and its own current name. Equal subcategory names under different parents are independent. A category's check already covers every child label, including labels no longer present among its suggestions.

Usage protection includes trashed receipt items and receipts. Making a suggestion inactive does not remove that protection. Matching uses the existing exact string semantics without trimming or case folding. Parent identity is resolved by ID, including a deleted parent when querying usage directly; there is no fallback to a global subcategory name. Renaming a suggestion can leave older snapshots that no longer match its current name. Recreating the same name pair can match those snapshots again.

A subcategory deletion conflict returns ProblemDetails with `detail`, `receiptItemCount`, and `affectedReceipts`. The count is matching **items**; the examples are up to 20 distinct **receipts**, using the same parent/name and trash policy, ordered by date descending and ID ascending. Each example contains `id`, `date`, `location`, and `isDeleted`. The client displays deleted receipts as text because their ordinary detail route is unavailable. Restore trashed items or receipts before editing their labels. The sample length cannot be subtracted from the item count to infer a remaining receipt count.

The controller checks usage before issuing deletion. These checks do not establish a transaction snapshot across the count and examples, or serialize concurrent receipt edits, taxonomy renames and deletion. Generic internal deletion services do not gain a new usage guard. This change fixes parent scoping without adding a global taxonomy lock or foreign keys to historical labels.
