# Spec Delta

## Purpose

Defines how each receipt item's envelope (where its money came from) is first recorded and then kept. Once recorded, an envelope changes only when a person acts. Classification may change freely, and no history already pushed to YNAB is ever rewritten.

## ADDED Requirements

### Requirement: Recorded and unresolved envelopes
An item's envelope SHALL be *recorded* when it names an envelope, and *unresolved* when it does not. `EnvelopeSource` SHALL record only provenance:
- `default`: filled from the item type's default, or set by a person choosing Automatic.
- `receipt`: applied from the receipt's paid-from.
- `item`: a person's per-item override.
- `historical`: set by the backfill.

#### Scenario: Provenance only
- **WHEN** an item's envelope was filled from its type's default
- **THEN** its source is `default`, and the source does not make the envelope follow later type changes

### Requirement: New items derive once
When a new item is saved, its envelope SHALL be derived once, taking the first available of:
- its override;
- the receipt's paid-from;
- the default for its item type, once the item is classified;
- unresolved.

Archived paid-from envelopes and archived defaults SHALL be skipped for new items.

#### Scenario: Paid-from wins over default
- **WHEN** a receipt paid from "Party" is saved with a "Grocery > Meat" item whose default is "Groceries"
- **THEN** the item records "Party" with source `receipt`

### Requirement: A recorded envelope is fixed against classification
A classification change SHALL NOT change a recorded envelope or its source, whatever the source and whichever path made the change. The paths are manual, approved mapping, the background worker, Jev, accepted assistance, cleanup apply and undo, and legacy write. Clearing an item's type SHALL keep its envelope.

#### Scenario: Reclassify with a recorded envelope
- **WHEN** an item with envelope "Groceries" (any source) is reclassified, by any path, to a type whose default is "Household"
- **THEN** its envelope stays "Groceries" with its source unchanged

#### Scenario: Clear keeps the envelope
- **WHEN** an item on a pushed receipt, with envelope "Groceries", is set to unclassified
- **THEN** its envelope stays "Groceries", and a later push of the receipt's remaining transactions is not blocked

### Requirement: Classification fills an unresolved envelope
When a classification change gives an unresolved item a type that has a default, the item SHALL record that default with source `default`. When the type has no default, the envelope SHALL stay unresolved, and its source SHALL be kept.

#### Scenario: Fill from default
- **WHEN** an item with no envelope is classified by Jev as a type whose default is "Groceries"
- **THEN** it records "Groceries" with source `default`

#### Scenario: No default
- **WHEN** an unresolved item with source `historical` is classified as a type with no default
- **THEN** it stays unresolved and keeps source `historical`

### Requirement: Bulk reclassification cannot move envelopes
Bulk classification paths SHALL NOT move any recorded envelope. That covers cleanup apply and undo, the mapping worker, and Jev. They SHALL only fill unresolved envelopes. An undo SHALL NOT clear an envelope that its apply filled.

#### Scenario: Cleanup over pushed items
- **WHEN** a cleanup campaign reclassifies 1,000 items on receipts already pushed to YNAB
- **THEN** no recorded envelope changes

### Requirement: Only a person changes a recorded envelope
A recorded envelope SHALL change only through one of these person-initiated actions:
- a per-item override;
- **Automatic**, which re-derives once from paid-from, else the type default;
- setting or clearing a receipt's paid-from;
- an envelope merge in an applied restructure plan, or its undo.

Automatic SHALL refuse with 409 and a `detail` rather than record an unresolved envelope.

#### Scenario: Automatic with nothing to derive
- **WHEN** a person chooses Automatic for an item whose receipt has no paid-from and whose type has no default
- **THEN** the server returns 409 with a `detail`, and the envelope is unchanged

#### Scenario: Paid-from applies to non-overrides
- **WHEN** a person sets a receipt's paid-from to "Party"
- **THEN** every item without an `item` override records "Party" with source `receipt`

### Requirement: Correcting a wrong first guess before push
After a person changes an item's type, the client SHALL offer "Also move to X?" when all of these hold:
- the receipt has never been pushed to YNAB;
- the new type's default X differs from the recorded envelope;
- the source is `default` or `historical`.

Accepting SHALL run Automatic. Declining SHALL leave the envelope as it was.

#### Scenario: Offer accepted
- **WHEN** a person corrects an item on an unpushed receipt from "Party Supplies" to "Grocery > Meat", whose default is "Groceries", and accepts the offer
- **THEN** the item records "Groceries" with source `default`

#### Scenario: Pushed receipt
- **WHEN** the same correction is made on a receipt with a YNAB transaction in any state other than not-synced
- **THEN** no offer is shown and the envelope is unchanged

### Requirement: Warning before drifting from YNAB
Before an override, Automatic or paid-from change on a receipt that has any YNAB transaction in a state other than not-synced, the client SHALL warn that YNAB will not be updated. After confirmation, the change SHALL be recorded locally only.

#### Scenario: Override on a pushed receipt
- **WHEN** a person overrides an item's envelope on a receipt already pushed to YNAB
- **THEN** they are warned that YNAB will not change, and after confirming, only the local envelope changes

### Requirement: Pushing never rewrites YNAB
Pushing to YNAB SHALL only create transactions. A transaction already created in YNAB SHALL NOT be changed by any later push, envelope change or classification change.

#### Scenario: Repush after a local change
- **WHEN** a person changes an envelope locally on a receipt whose transaction is already synced, and pushes again
- **THEN** the synced YNAB transaction is not modified

### Requirement: Default changes never move history
Changing or removing an item type's default envelope SHALL NOT change any item's recorded envelope.

#### Scenario: Default changed
- **WHEN** an Admin changes the default for "Grocery" from "Groceries" to "Food"
- **THEN** items already recorded with "Groceries" keep it
