# Category assistance review preferences

Category assistance review preferences are deployment-local operational state in
`category_assistance`. They do not change a suggestion, classification, category
mapping, provider consent, or portable backup.

Each authenticated active writer has an independent preference for each completed
suggestion. The default is `active`, revision `00000000-0000-0000-0000-000000000000`,
with no excluded receipt items. `later` defers the suggestion from the default
list, `left_as_is` removes it from the default list, and `active` reopens it.
`GET /api/category-assistance/suggestions` accepts `review=active|later|left_as_is|all`.
The single-suggestion URL stays readable in any review state. List rows expose
the actor's review state, revision, and excluded item count.

`PUT /api/category-assistance/suggestions/{attemptId}/review-preference` replaces
the review state and the complete excluded receipt item ID set. The set is capped
at 1000 distinct IDs. Clients send an `operationId` and `expectedRevision`; the
first write expects the zero revision. A revision mismatch returns 409. The
operation ledger records the request hash and response permanently, so replaying
an older operation returns its original response without restoring its state.
Reusing an operation ID with another payload or attempt returns 409.

Exclusions affect only the same actor's eligible item page, affected count, and
assistance acceptance. An attempt to accept a still excluded item returns 409.
Other actors and ordinary manual classification keep their normal behavior.
Both preference writes and acceptance lock the attempt row, which orders their
checks and prevents a concurrent exclusion update from slipping past acceptance.

The migration creates `ReviewPreferences` and `ReviewOperations` in the
`category_assistance` schema. Neither table is part of portable backup data.
The downgrade refuses to discard populated review state or operation history.
