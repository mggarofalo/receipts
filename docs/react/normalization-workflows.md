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
Receipt entry and persisted-item forms share `ReceiptNormalizationField`. Its
preview reads approved exact-source rules and searchable active catalog names;
catalog choices are not model-ranked suggestions. A new product is only an intent
until the receipt is saved atomically. Choosing a product never rewrites the
original receipt description or financial fields.

Choices bind to store, description and optional item code. Changing those inputs
clears the choice, including changing away and back. Preview requests debounce and
cancel; late responses cannot supply evidence for another source. The client
captures observed mapping, target and item revisions rather than silently updating
a submitted choice after a refetch. The server makes the final validity decision.
An existing reviewed purchase retains its one-off assignment during financial-only
edits. Ambiguous source descriptions permit only per-purchase choices, never a new
remembered rule.

The default explicit choice applies to this purchase only. Remembering an exact
source requires an affirmative checkbox. Existing rule changes and historical
scope remain separate curation operations, reachable without abandoning the draft.
Lookup failures and unknown descriptions do not disable financial save: the server
persists unresolved purchases for review. Saved status comes from the returned
items, not optimistic preview state. Conflicts retain the form and offer recovery;
pending-name collisions link to the exact review target in a new tab. Mutation
callbacks are scoped to their dialog session, so an old completion cannot close a
new draft. Unchanged retries reuse the request key; changed payloads receive a new
key.

Inline subcategory creation deduplicates by category and normalized name, with each
request promise owning its cleanup so overlapping requests and failures can retry.

Projection repair remains centralized in `query-invalidation.ts`. Duplicate
acceptance changes refresh the duplicate list, accepted list and health summary.
Receipt and transaction changes already repair the entire reports family.

Catalog recovery uses `reviewTarget` for an exact pending identity and `reviewQ` for a server-side queue search. The results component is keyed by that navigation state so changing the target or search disposes curation dialogs and resets pagination. `catalogSource` retains only the source key for returning to the catalog. No navigation action activates an identity. Exact-target cache entries share the normalized-description invalidation prefix, so explicit activation refreshes the source's label-only eligibility.
