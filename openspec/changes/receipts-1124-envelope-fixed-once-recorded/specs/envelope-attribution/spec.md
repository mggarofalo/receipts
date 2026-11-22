# Spec Delta

## MODIFIED Requirements

### Requirement: Only a person changes a recorded envelope
A recorded envelope SHALL change only through one of these person-initiated actions:
- a per-item override;
- **Automatic** (`POST /api/receipt-items/{id}/envelope/automatic`), which re-derives once from paid-from, else the type default;
- setting or clearing a receipt's paid-from;
- an envelope merge in an applied restructure plan, or its undo.

Automatic SHALL refuse with 409 and a `detail` rather than record an unresolved envelope.

#### Scenario: Automatic with nothing to derive
- **WHEN** a person chooses Automatic for an item whose receipt has no paid-from and whose type has no default
- **THEN** the server returns 409 with a `detail`, and the envelope is unchanged

#### Scenario: Paid-from applies to non-overrides
- **WHEN** a person sets a receipt's paid-from to "Party"
- **THEN** every item without an `item` override records "Party" with source `receipt`

## ADDED Requirements

### Requirement: Automatic action contract
`POST /api/receipt-items/{id}/envelope/automatic` SHALL re-derive the item's envelope once, whatever its current source. It SHALL use the receipt's active paid-from, recording source `receipt`. Otherwise it SHALL use the item type's default, recording source `default`. It SHALL leave the item's occasion and classification unchanged, and SHALL require receipt write authorization. An unknown item SHALL return 404.

#### Scenario: Override returns to the default
- **WHEN** an item with an `item` override of "Party", on a receipt with no paid-from, is set to Automatic and its type's default is "Groceries"
- **THEN** it records "Groceries" with source `default`

#### Scenario: Default-sourced item takes the paid-from
- **WHEN** an item with source `default` is set to Automatic on a receipt whose paid-from is "Party"
- **THEN** it records "Party" with source `receipt`

#### Scenario: Occasion untouched
- **WHEN** an item tagged with occasion "Birthday" is set to Automatic
- **THEN** its occasion is still "Birthday"

### Requirement: A null envelope choice keeps the envelope
On `PUT /api/receipt-items/{id}/budget-choices`, a non-null `envelopeId` SHALL record a per-item override with source `item`. A null or omitted `envelopeId` SHALL leave the recorded envelope and its source unchanged. Returning an item to automatic SHALL happen only through the Automatic action.

#### Scenario: Occasion-only edit
- **WHEN** a client sets an item's occasion and sends `envelopeId: null`
- **THEN** the occasion changes, and the item's envelope and source are unchanged

#### Scenario: Null does not remove an override
- **WHEN** a client sends `envelopeId: null` for an item with an `item` override
- **THEN** the override remains

### Requirement: Split comparison uses recorded envelopes
The YNAB split comparison for a receipt SHALL compute its expected split from each item's recorded envelope, and compare that with what YNAB holds. It SHALL NOT offer a view that re-derives envelopes from the current taxonomy or from defaults. The endpoint SHALL accept no category-view parameter.

#### Scenario: Recorded envelope differs from today's default
- **WHEN** an item records "Groceries" while its type's current default is "Household"
- **THEN** the expected split places the item under the YNAB category linked to "Groceries"

#### Scenario: No preview selector
- **WHEN** a person opens a receipt's split comparison
- **THEN** no current-hierarchy preview option is shown
