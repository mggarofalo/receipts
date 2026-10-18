# Normalization and receipt draft interactions

The normalized-description page owns navigation. Review and registry tabs own the
selected operation and its source snapshot. Each curation dialog is mounted only
for that operation, keyed by source ID, and unmounted on close. Drafts, debounced
search and mutation observers therefore cannot outlive their dialog session.
Do not keep hidden mutation-bearing dialogs mounted: an earlier completion must
not close or reset a later operation.

Merge and template-link choices require a successful current query, a settled
search, no pending fetch, and membership in the displayed candidate set. Changing
search clears selection. Query errors belong to the picker with a retry action;
they are not empty results. The server still validates status and ownership when
the write executes.

Split selections intentionally span pages. A current successful page that no
longer contains a previously selected item blocks submission and offers a reset.
Disproven selection evidence survives later loading, failures and page navigation
until the operator clears or reselects it. Other pages do not erase selections. A failed or pending read blocks submission;
server ownership checks remain authoritative across pages and concurrent writes.
Inline rename keyboard actions honor the same pending-write lock as its buttons.
Selection/name updates happen in the interaction handler, not inside state updater
callbacks. User-entered names survive later selection changes.

Settings previews and test results display their immutable submitted inputs. An
edited threshold or description does not change what an earlier response means.
Maintenance confirmation captures the preview fingerprint and counts when opened;
a later preview refresh cannot silently replace the set the operator confirmed.
The server rejects a stale fingerprint.

Receipt-item interaction validation lives in `receipt-item-draft.ts`. Add and inline
edit use the same schema, including trimmed text and finite numeric validation.
The standalone persisted-item form extends it to require item code and subcategory;
the wizard keeps those optional. Inline edit preserves the existing item code.
Server domain validation and shared financial arithmetic remain separate owners.
Inline subcategory creation deduplicates by category and normalized name, with each
request promise owning its cleanup so overlapping requests and failures can retry.

Projection repair remains centralized in `query-invalidation.ts`. Duplicate
acceptance changes refresh the duplicate list, accepted list and health summary.
Receipt and transaction changes already repair the entire reports family.
