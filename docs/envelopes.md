# Envelopes and occasions

An item's **type** and its **envelope** answer different questions. The type says
what the thing is ("meat"). The envelope says where the money came from ("the
party budget"). Buying meat for an event can come out of the event's envelope,
but it is still meat. The taxonomy keeps describing types; envelopes are a
separate library.

## The model

| Concept | Where | Notes |
|---|---|---|
| Envelope | `budget.Envelopes` | `Name`, `IsActive`. Archived (inactive) envelopes stay on past purchases but cannot be chosen. Soft delete and audit like other library rows. |
| Occasion | `budget.Occasions` | An optional tag (an event, a holiday, a gift). Never required at entry. |
| Item-type default | `budget.ItemTypeEnvelopeDefaults` | Taxonomy category → envelope. Resolved by the nearest mapped ancestor, like YNAB resolution used to be. A default whose envelope is archived is skipped. |
| YNAB link | `ynab.YnabEnvelopeMappings` | Envelope → YNAB category, one per envelope and budget. |
| Item | `ReceiptItems.EnvelopeId`, `EnvelopeSource`, `OccasionId` | The envelope recorded when the item was saved. Null is unresolved. |
| Receipt | `Receipts.PaidFromEnvelopeId`, `OccasionId` | Optional receipt-level choices applied to its items. |

`EnvelopeSource` records why an item has its envelope:

- `default` — its item type's default;
- `receipt` — the receipt's "paid from" envelope;
- `item` — a per-item override;
- `historical` — written by the [backfill](migrations/20261030134404_AddEnvelopes.md).

## How an item gets its envelope

The rule lives in one place, `Application.Envelopes.EnvelopeDerivationPolicy`:

> item override ?? receipt paid-from ?? default(item type) ?? unresolved

Persistence applies it at the write chokepoints (`Infrastructure.Services.ItemEnvelopeWriter`):

- **New items** take the override, else the receipt's paid-from envelope, else
  the default for their item type once it is classified. A paid-from envelope
  or occasion archived after the receipt was saved is skipped for items added
  later, as archived defaults are.
- **Item-type changes.** Every classification write appends through
  `ClassificationService.AppendAsync`, whichever path made it: manual choice,
  approved mapping at save, the background approved-mapping worker, historical
  reclassification, category assistance or a legacy-label save. There, an item
  whose source is `default`, or whose envelope is still unresolved, takes the
  new type's default. `receipt`, `item` and `historical` envelopes are kept.
  A [category restructure](restructure-plans.md) is the exception: it changes
  what a purchase is, not who paid, so it keeps every recorded envelope. Only
  an envelope merge in the plan moves purchases between envelopes.
- **Ordinary edits** keep the recorded envelope and occasion unless the request
  names new ones.
- **Changing a receipt's paid-from envelope** applies it to every item except
  per-item overrides. Clearing it sends receipt-sourced items back to their
  default. A receipt occasion replaces the occasion only on items that carried
  the previous one.
- **Explicit choices** (`budget-choices` below) replace the stored ones, so
  null can clear them. Returning an overridden item to automatic takes the
  receipt's active paid-from envelope, else its item type's default. Any other
  item keeps its recorded envelope.
- A newly chosen archived or missing envelope or occasion is rejected (400).
  Keeping one already stored is allowed.

The envelope is recorded, not recomputed when read. Changing a default never
moves history, which matches the taxonomy's "as classified" stance. Changing
only an item's envelope or occasion keeps its normalization revision, so
budget attribution does not invalidate normalization or classification evidence.

## YNAB

Push and the split comparison resolve each item's YNAB category from its
recorded envelope's link in the selected budget. A null envelope, or an
envelope with no link, is unmapped and blocks the push, as an unmapped category
did before. Splits group by YNAB category, so many envelopes can share one.

The "current hierarchy" split comparison previews what saving would do today:
items that follow their item type are re-derived from the current tree and
defaults; the others keep their recorded envelope.

Node-keyed resolution is retired. The old `ynab.YnabCategoryMappings` rows stay
as frozen evidence for the backfill, the parity check and portable backups.
Nothing writes them any more.

## API

| Endpoint | Purpose |
|---|---|
| `GET/POST /api/envelopes`, `GET/PUT/DELETE /api/envelopes/{id}` | List, create, rename or archive (`isActive: false`), delete. Delete answers 409 while purchases, receipts or defaults use the envelope; archive it instead. |
| `GET/POST /api/occasions`, `GET/PUT/DELETE /api/occasions/{id}` | The same for occasions. |
| `GET /api/item-type-envelope-defaults`, `PUT/DELETE /api/item-type-envelope-defaults/{categoryId}` | Item-type defaults. The item type must be selectable in the current taxonomy. |
| `GET /api/ynab/envelope-mappings`, `GET /api/ynab/envelope-mappings/unmapped`, `PUT/DELETE /api/ynab/envelope-mappings/{envelopeId}` | YNAB links for the selected budget. |
| `PUT /api/receipts/{id}/budget-choices` | Set the receipt's paid-from envelope and occasion. Null is automatic / none. |
| `PUT /api/receipt-items/{id}/budget-choices` | Set an item's envelope override and occasion. Null is automatic / none. |

Receipt and item responses expose `paidFromEnvelopeId`, `occasionId`,
`envelopeId` and `envelopeSource`. Receipt and item writes accept optional
`paidFromEnvelopeId`, `occasionId` and `envelopeId` (a per-item override).
Omitted or null keeps the stored choice on update; use the `budget-choices`
endpoints to clear one.

## Admin UI

**Library → Envelopes** (`/settings/envelopes`) manages envelopes, occasions
and item-type defaults. **YNAB settings** links each envelope to a YNAB
category.

## Receipt entry

Envelopes stay automatic; nothing asks for one. The new-receipt page and the
receipt's items card carry a quiet **Paid from: Auto** selector and an optional
**Occasion** chip. Choosing an envelope applies it to every item without its
own override. Each item row shows its item type and, only where its envelope
differs from the receipt's, a small envelope badge. The receipt's envelope is
its paid-from choice, else the one most of its items share. A typed item with no
envelope shows **No envelope**. The row menu (**⋯**) holds the per-item
override; **Automatic** removes it.

Before a receipt is saved the page previews each item's envelope with the same
rule (`lib/envelope-display.ts`); the server records the real value at save.

## Portable backups

Format 15 adds envelopes, occasions, defaults, envelope links, the item and
receipt choices and the backfill marker. Restoring format 14 or older derives
envelopes exactly as the deploy backfill does. See [backup and restore](backup-restore.md).
