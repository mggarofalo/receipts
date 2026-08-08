# Item Vocabularies — Templates, Normalized Descriptions, and Similarity

Why this app has more than one notion of "what an item is", which one wins where, and what the
rules are when they disagree. Written for RECEIPTS-881; read alongside
[normalized-description-review.md](normalized-description-review.md), which covers the review
queue itself.

## Three surfaces over the same data

| Surface | Origin | Purpose | Lives in |
| --- | --- | --- | --- |
| **Item template** | User-curated | Entry-time defaults: name, category, subcategory, unit price, item code | `library.ItemTemplates`, `/item-templates` |
| **Normalized description** | Machine-derived | The reporting vocabulary — what spending is grouped by | `matching.NormalizedDescriptions`, `/admin/normalized-descriptions` |
| **Item similarity** | Machine-derived, read-only | Suggestions while typing a line item | `ItemTemplateSimilarityService`, `useSimilarItems` |

The third is worth naming explicitly because it is easy to mistake for a fourth vocabulary. It is
not: `GetSimilarItems` and `GetCategoryRecommendations` compute suggestions on demand and store
nothing. They influence what a user picks, never what anything resolves to. Only the first two
own persisted identity, and only they can disagree.

## The problem: two vocabularies that never met

Templates and normalized descriptions had no FK, no shared lookup, and no cross-reference. A line
item created *from a template* went through the ANN resolver as unknown text like any other, and
could land in the review queue — asking a human to confirm a grouping the same human had already
declared by creating the template.

The registry could therefore end up holding a machine-derived entry for text a user had already
named, differing only in whitespace or capitalisation, with spending split across both.

## Decision: cross-link them; the template wins at entry time

Both concepts survive with distinct roles. Templates are curated entry-time defaults; normalized
descriptions are the reporting vocabulary. `ItemTemplate` gains an optional FK to
`NormalizedDescription`, and items created from a template are stamped with it at write time.

Rejected alternatives:

- **Collapse to one vocabulary.** Forces every canonical entry to carry template baggage —
  category, subcategory, price defaults — for the thousands of entries the resolver derives on its
  own and no user will ever curate.
- **Evidence-only hints** ("this looks like template X"). Leaves the duplication in place and
  still asks a human to confirm what they already said.

### Precedence

**A template's declaration beats the classifier's inference, at entry time only.**

Concretely: `CreateReceiptItemRequest.itemTemplateId` causes the item to be written with the
template's `NormalizedDescriptionId` already set. The resolver only considers items
`WHERE NormalizedDescriptionId IS NULL`, so it skips them — no second predicate was added, and
none should be. A template-stamped item that later needs re-resolving can simply have its FK
cleared and it re-enters the queue naturally.

"At entry time only" is the limit of the rule. Once the item is written, it is an ordinary
receipt item: merge, split, reject and rename all treat it exactly like a resolver-derived one.
The template does not get a veto over later admin decisions.

### No match score is fabricated

A stamped item gets `NormalizedDescriptionMatchScore = null`. The column records a cosine
similarity, and nothing was compared — this is a declaration, not a match. A fabricated `1.0`
would be indistinguishable from a perfect ANN hit in `PreviewThresholdImpactAsync`, which buckets
items by exactly that column, so every future threshold preview would be computed partly from
scores that never came from a comparison.

## Creating a template creates its canonical entry

`GetOrCreateForTemplateAsync` is deliberately *not* `GetOrCreateAsync`. The two answer different
questions:

- `GetOrCreateAsync` asks "what does this receipt text probably mean?" — ANN search, threshold
  bands, possibly the review queue.
- `GetOrCreateForTemplateAsync` records what a user already told us. Exact-match-or-create only,
  always `Active`.

The embedding is still generated. That is the point of the whole exercise: the template teaches
the registry a name, and the registry then recognises *the same item typed freehand* on a later
receipt without the template being involved.

### Status interactions

| Existing entry for that name | What happens | Why |
| --- | --- | --- |
| `Active` | Reused | Nothing to decide |
| `PendingReview` | Linked, **left pending** | The template says what the item is *called*. It does not vouch for the resolver's grouping of whatever raw text landed on that row, and that grouping is what review is for. |
| `Rejected` | **Reinstated to `Active`**, audited | Creating a template for tombstoned text is a deliberate, later contradiction of the rejection, and the explicit action wins. Refusing would leave the user unable to name their own item with nowhere to discover why — tombstones appear on no screen they would look at. Audited rather than silent, because it reverses a recorded decision. |

## Keeping the link honest

The FK is `ON DELETE SET NULL`, mirroring `ReceiptItem`'s FK to the same table. That is a
**backstop, not the mechanism**:

- **Merge** re-points templates explicitly, including soft-deleted ones. Letting the database null
  the column would silently unlink every template pointing at the merged-away row and quietly put
  its items back through the resolver, with nothing raised anywhere. Same class of bug as the
  soft-deleted-transaction stranding in RECEIPTS-801.
- **Reject** unlinks templates but does not delete them. A template still pointing at a tombstone
  would go on stamping new receipt items with it — the resolver bypass working against the
  reviewer's decision. The template survives because rejecting a canonical entry is a judgement
  about *receipt text*, not about somebody's curated entry-time defaults; destroying their template
  as a side effect of an admin action on another screen would be a far bigger surprise than an
  unlinked one.
- **Client-side**, the template id is dropped the moment the description stops matching the
  template's name (trimmed, case-insensitive). Otherwise "Gallon of Milk" edited to "Orange Juice"
  would still be stamped with the milk entry and filed under milk in every report. Erring towards
  dropping is deliberate: an unstamped item just goes through the resolver as it always did,
  whereas a wrongly-stamped one is silently misfiled with nothing to reveal it.

Clearing the field and retyping the same name also drops the link, and nothing restores it. Left
alone on purpose — such an item falls through to the resolver, which exact-matches the very entry
the template created and links it anyway. Same outcome, different route. Restoring the link from a
string match would mean re-deriving provenance by guesswork, which is what this issue removes.

## Existing templates link lazily, not by backfill

The migration adds the column empty. Templates that predate this link on their next create or
update. Two reasons, both load-bearing:

1. **A migration cannot generate embeddings.** The ONNX embedding service lives behind DI and is
   not available to `dotnet ef database update`, so anything backfilled would carry a NULL vector.
   Such a row exists in the registry but is invisible to every ANN search — so the same item typed
   freehand later would never match it. The entry would look linked while doing none of the work
   the link exists for.
2. **It would invent `Active` entries for templates nobody has used**, including the four seeded
   demo rows, on every fresh install. Those show up in the registry with 0 linked items and
   "Last Seen: Never" (RECEIPTS-880) — dead weight indistinguishable from the dead weight that
   column exists to help an admin find.

An unlinked template is a supported state, not a defect: it behaves exactly as it did before this
issue.

## Failure is a no-op, everywhere

Every path here degrades to "as before" rather than to an error:

- Linking fails while saving a template (embedding service down, registry unavailable) → the
  template still saves, unlinked. The link is a convenience; refusing to save someone's template
  because the classifier is unavailable trades a working feature for a bookkeeping one.
  Cancellation is *not* swallowed — the caller has gone away.
- Unknown, soft-deleted, or not-yet-linked `itemTemplateId` on a receipt item → the item is written
  unstamped and the resolver picks it up. The id is a hint about provenance, not a constraint the
  receipt has to satisfy.

## Surfacing the link to a reviewer

Added in **RECEIPTS-930**, on top of the schema link above.

### "Declared by template X" is evidence, not decoration

Every canonical row now carries the template that declares it — read off the FK, never inferred
from the name, so the badge can only ever say *these are linked* and never *these look alike*.
`NormalizedDescriptionDetail` gains `LinkedTemplateId` / `LinkedTemplateName` /
`LinkedTemplateCount`, projected as correlated subqueries in the same single query as `LastSeen`.

It reads as the opposite thing in the two places it appears:

- **Review queue** — a nudge. Everything else on a pending row is a machine's opinion; this is the
  one fact a human put there. A pending row with a template is almost always an Approve.
- **Registry** — a warning. An entry somebody curated should not be merged away or sent back to
  review on the same impulse as resolver output.

The count is surfaced rather than collapsed because **more than one template per row is normal, not
anomalous**: merging two template-backed entries leaves both templates on the survivor, and
`MergeAsync` re-points them on purpose. Naming one and implying it is the only one would hide
exactly the case an admin most needs to see. Soft-deleted templates are excluded by the entity's
query filter — a template in the recycle bin is not evidence of anything.

### "Link to template" consolidates; it does not just set the FK

The obvious reading of the action is "point the template's FK at this row". That is what the issue
asked for, and it is wrong twice over:

1. **It would not survive.** `ItemTemplateService.UpdateAsync` re-resolves the link from the
   template's *name* on every save. The next edit to that template — a price change, a category fix,
   anything — would silently point it back at its own entry. A link that disappears on an unrelated
   edit is worse than no link, because nobody would connect the two events.
2. **It would not do what the reviewer wants.** Pointing the FK affects items entered from the
   template *in future*. The receipt items already sitting on the row stay where they are, so the
   two go on reporting as separate buckets — which is the duplication being complained about.

So `LinkTemplateAsync` resolves the template's entry (creating it when the template has never been
linked), points the template at it, and then consolidates the caller's row into it via `MergeAsync`
— same re-linking, re-scoring, trashed-item handling and audit trail as any other merge. The
surviving row is the template's, because that is the row the name invariant will keep re-deriving.

Consequences, stated because they are not free:

- **The row the caller pointed at usually stops existing.** The response says which happened
  (`merged`), the dialog predicts it per selection using the same exact-match rule the server uses,
  and the toast reports what actually occurred rather than what was predicted.
- **A rejected row cannot be linked.** Consolidating a tombstone away would delete the record of a
  reviewer's decision and free the resolver to recreate the text. `GetOrCreateForTemplateAsync` does
  reinstate a tombstone — but only the one whose name the user typed as their template, which is
  them contradicting the rejection deliberately. Reaching a differently-named tombstone from the
  review queue is not that.
- **The merge-recurrence caveat applies**, as it does to every merge here: the consolidated row's
  matched text no longer exists, so a later receipt carrying it goes back through the resolver and
  may re-enter the queue. Not made worse by this action, and not solved by it.

The template picker filters in the browser over a capped page, because `/api/item-templates` has no
search parameter and adding one is a change to another module's contract. Safe only because the cap
is disclosed on screen — for a machine-generated list this would be the silent truncation
RECEIPTS-878 fixed in the merge dialog. If the template list ever grows past browsing size, that
endpoint needs a `q`.

## Decision: approving does *not* offer to promote into a template

Considered in RECEIPTS-930 and **declined**. Four reasons, in order of how badly it fails:

1. **It would create the duplicate this whole design exists to prevent.** A promoted template takes
   the row's *display* name, and `GetOrCreateForTemplateAsync` resolves a template to the entry
   whose *canonical* name matches. For any row that has been renamed — the exact rows an admin has
   curated enough to want a template for — those differ, so promoting mints a second canonical entry
   and points the new template at *that*. The row you promoted from is untouched, and you are left
   with an empty duplicate. The feature would be correct only for rows that have not used the
   adjacent feature.
2. **It conflates the two vocabularies this document exists to separate.** Approving says "this
   grouping is right for reporting". Creating a template says "I will type this item again and want
   defaults". Neither implies the other, and the rejected-alternatives list above already turned
   down collapsing them.
3. **The defaults would be guesses presented as declarations.** Category, subcategory and unit price
   would have to be inferred from the linked receipt items — a modal category, some average price —
   and written into the table whose entire purpose is to hold what a user stated by hand.
4. **Volume.** The registry is machine-derived and grows without bound; templates are hand-curated
   and shown in a picker during data entry. Attaching an offer to the most-repeated admin action in
   the app is an invitation to fill that picker with receipt text.

What exists instead is the reverse direction, which is already correct: creating a template creates
its canonical entry (above), and a reviewer who recognises a row as a template's item links it
(above). Both routes end with one entry and one template pointing at it, which is the outcome
promotion was reaching for.
