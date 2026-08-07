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

## Still open

Deferred to **RECEIPTS-930**, and deliberately not attempted here:

- Showing "matches template X" as review-queue evidence, with "link to template" as a row action.
- Whether approving a normalized description should offer to promote it into a template, carrying
  category/subcategory/price defaults.

Both are UI surfaces over the link this issue creates, and neither is needed for the link to be
correct. Landing the schema, the entry-time rule, and the merge/reject interactions first keeps
the risky part — a new FK on the receipt write path — separable from presentation work.
