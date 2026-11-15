# purchase-classification Specification

## Purpose

Defines what type a receipt item is and how that is recorded: an append-only history of assignments, the sources a writer may record, and the eligibility and undo rules shared by every writer. It also covers how a type change affects an item's envelope.

## Requirements

### Requirement: Append-only assignment history
Every classification change SHALL append a new assignment and SHALL never edit or delete an earlier one. An assignment SHALL record:
- the item;
- the category, or null;
- the taxonomy revision it pins;
- a UTC recorded-at time and a per-item sequence;
- the previous assignment;
- the actor, source and reason.

The item's current assignment SHALL be the latest one.

#### Scenario: Correction appends
- **WHEN** a person changes an item from "Dairy" to "Bakery"
- **THEN** the item has two assignments, the "Dairy" one is unchanged, and the "Bakery" one points to it as previous

### Requirement: Recorded path is preserved
An assignment SHALL keep the path it was recorded with, as given by its pinned revision. A later rename or move of the category SHALL NOT change that path.

#### Scenario: Category renamed later
- **WHEN** an item was classified as "Grocery > Dairy" and "Dairy" is later renamed "Dairy & Eggs"
- **THEN** the item's history still shows "Grocery > Dairy" for that assignment

### Requirement: Source vocabulary
Each assignment's source SHALL be one of `manual`, `approved_mapping`, `jev`, `restructure`, `legacy_import` or `legacy_write`. The server SHALL set the source and actor; a client SHALL NOT supply them. Assignments of unknown legacy origin SHALL NOT be presented as approvals.

#### Scenario: Client cannot set source
- **WHEN** a client sends a classification write that includes a source field
- **THEN** the stored source is set by the server, not taken from the request

### Requirement: Person-made changes record manual
A change a person makes SHALL be recorded with source `manual`. That covers a direct choice, accepting a Jev suggestion, applying a cleanup campaign, and undoing any recorded operation.

#### Scenario: Accepted suggestion
- **WHEN** a person accepts Jev's suggestion "Household > Cleaning"
- **THEN** the new assignment has source `manual`

#### Scenario: Undo
- **WHEN** a person undoes an approved-mapping operation
- **THEN** each reverted item gains an assignment with source `manual`

### Requirement: Valid targets
A chosen category SHALL be active in the pinned revision. An archived or absent category SHALL be refused with 400 and a `detail`. A parent category SHALL be a valid assignment in its own right. Null SHALL mean the item is unclassified, never a placeholder category. A legacy category literally named `Uncategorized` SHALL remain a named category, distinct from null.

#### Scenario: Broad assignment
- **WHEN** a person classifies an item as "Grocery", which has children
- **THEN** the assignment is accepted with category "Grocery"

#### Scenario: Archived target
- **WHEN** a person submits a category that is archived in the current revision
- **THEN** the server returns 400 with a `detail`

### Requirement: Explicit classification write
`PUT /api/receipt-items/{id}/classification` SHALL require `categoryId` (nullable), `taxonomyRevisionId`, `expectedAssignmentId` (null for an item's first assignment), `operationId` and `reason`. If either the revision or the expected assignment is stale, it SHALL return 409 with a `detail`. A replay SHALL follow the same operation-ID rule as taxonomy edits.

#### Scenario: Stale expected assignment
- **WHEN** a person submits a write whose `expectedAssignmentId` is no longer the item's current assignment
- **THEN** the server returns 409 with a `detail`, and no assignment is appended

#### Scenario: Explicit clear
- **WHEN** a person submits `categoryId: null` with current expectations
- **THEN** a `manual` assignment with no category is appended

### Requirement: Saves preserve classification unless asked
An item or receipt save that omits classification intent SHALL leave the current assignment unchanged. The legacy `category` and `subcategory` request fields SHALL be accepted and ignored. They SHALL never be read to classify, compared for contradictions, displayed, searched or sorted.

#### Scenario: Price-only edit
- **WHEN** a person edits an item's price and sends no classification intent
- **THEN** the item's current assignment is unchanged

#### Scenario: Legacy strings ignored
- **WHEN** a client sends `category: "Food"` on an item save
- **THEN** the item's classification is unaffected

### Requirement: New item with no choice
A new item saved without classification intent SHALL take a valid approved mapping for its exact source if one exists. Otherwise it SHALL take an unresolved `legacy_write` assignment. Saving SHALL never call a provider, and SHALL NOT require any item to be classified.

#### Scenario: Mapping exists
- **WHEN** a receipt is saved with an item whose exact source has a current approved mapping
- **THEN** the item gets an `approved_mapping` assignment to the mapped category

#### Scenario: No mapping
- **WHEN** a receipt is saved with an unmapped item and no choice
- **THEN** the save succeeds and the item gets an unresolved `legacy_write` assignment

### Requirement: Automation eligibility
An automatic writer (the approved-mapping worker, or Jev) SHALL write only to an item that has no type. That means:
- no assignment;
- `legacy_import` or `legacy_write` with no resolved category;
- a `restructure` assignment with no category.

It SHALL never write over a `manual` assignment, including a manual clear, or over any assignment that has a category.

#### Scenario: Manual clear is protected
- **WHEN** a person cleared an item to unclassified, and a mapping for its source is later approved
- **THEN** the mapping worker does not classify that item

#### Scenario: Restructure-cleared item is eligible
- **WHEN** a restructure left an item with a `restructure` assignment and no category
- **THEN** the item is eligible for the mapping worker and for Jev

### Requirement: Stored legacy labels do not affect eligibility
An item's eligibility for automation SHALL depend only on its current assignment. Legacy label strings stored on the item SHALL NOT exclude it from automation.

#### Scenario: Labelled unresolved item
- **WHEN** an unresolved `legacy_import` item still stores the label "Groceries"
- **THEN** it is eligible for the mapping worker and Jev like any other untyped item

### Requirement: Conditional correction
An undo of any recorded operation SHALL append the prior value to an item only while the item's current assignment is still the one that operation wrote. A later change blocks the undo for that item, even a change to the same category. Undo SHALL write against a freshly captured taxonomy revision. Each item's outcome SHALL be recorded durably as applied, conflicted or skipped.

#### Scenario: Later edit blocks undo
- **WHEN** an operation set an item to "Snacks", a person then set it to "Snacks" again by hand, and the operation is undone
- **THEN** that item is reported conflicted and keeps the person's assignment

#### Scenario: Prior category archived
- **WHEN** an undo would restore a category that is now archived
- **THEN** that item is reported conflicted, and the tree is not changed

### Requirement: Type changes only fill an unresolved envelope
A classification change SHALL NOT change an item's recorded envelope, whatever path made the change. When the item has no envelope, the change SHALL fill it from the new type's default if one exists. See `envelope-attribution` for the full rules.

#### Scenario: Recorded envelope kept
- **WHEN** an item with envelope "Groceries" is reclassified to a type whose default is "Household"
- **THEN** its envelope stays "Groceries"

#### Scenario: Clearing keeps the envelope
- **WHEN** an item with envelope "Groceries" is cleared to unclassified
- **THEN** its envelope stays "Groceries"

### Requirement: Display uses the recorded path
Receipt chips, item search, item-code suggestions and the item form SHALL name the path recorded on the item's current assignment, read from that assignment's own revision. Item search SHALL also match on ancestor names.

#### Scenario: Search by parent
- **WHEN** a person searches items for "Grocery"
- **THEN** items classified as "Grocery > Dairy" are included

### Requirement: Classification history is readable
Any authenticated user SHALL be able to read an item's full classification history, with pagination.

#### Scenario: Paged history
- **WHEN** a signed-in person requests an item's history with a page size of 10
- **THEN** they receive up to 10 assignments and a way to fetch the next page

### Requirement: Classification authorization
Explicit classification of an item SHALL require the same authorization as writing its receipt. The server SHALL enforce this equally for API keys.

#### Scenario: Read-only key
- **WHEN** an API key without receipt-write permission submits a classification write
- **THEN** the server returns 403

### Requirement: Classification write switch
With `Classification:WritesEnabled=false`, the server SHALL refuse explicit classification writes. Ordinary receipt and item saves without classification intent SHALL still succeed.

#### Scenario: Writes disabled
- **WHEN** classification writes are disabled and a person saves a receipt without classification intent
- **THEN** the save succeeds

### Requirement: Uncategorized items list
**Reports → Uncategorized items** SHALL list every live item that has no type, whatever the state of any Jev attempt. That means no assignment, cleared, or unresolved. It SHALL work with no provider configured. Choosing a type on a row SHALL write a `manual` assignment and remove the row. Selecting several rows SHALL apply one type to all of them.

#### Scenario: Fix one row
- **WHEN** a person chooses "Household" on an uncategorized row
- **THEN** a `manual` assignment is written and the row leaves the list

#### Scenario: No provider
- **WHEN** no assistance provider is configured
- **THEN** the list still shows every untyped item, and manual and bulk fixes work

### Requirement: Classification and catalog identity are independent
A classification change SHALL NOT change an item's normalized product (catalog) link. A catalog change SHALL NOT change an item's classification.

#### Scenario: Reclassify a linked item
- **WHEN** a person reclassifies an item linked to a catalog product
- **THEN** the catalog link is unchanged
