# approved-category-mappings Specification

## Purpose

Defines "remember this category for this exact source": an Admin-approved rule mapping one exact store, description and item code to an item type. New purchases from that source pick it up when saved, and a bounded background worker applies it to older untyped purchases.

## Requirements

### Requirement: Exact source key
A mapping SHALL identify one exact source: the store location with outer U+0020 spaces trimmed, the description, and an optional item code. A difference in case, Unicode, tabs, item code or location SHALL make a different source. A missing item code SHALL mean "no code", never "any code".

#### Scenario: Different code
- **WHEN** a mapping exists for "MILK 2%" at "Costco" with code 1234
- **THEN** "MILK 2%" at "Costco" with code 5678 is not matched by it

#### Scenario: Missing code is not a wildcard
- **WHEN** a mapping exists for "MILK 2%" at "Costco" with no code
- **THEN** "MILK 2%" at "Costco" with code 1234 is not matched by it

### Requirement: Admin approval with revision checks
Only an Admin SHALL approve, change or revoke a mapping; anyone else SHALL receive 403. A change SHALL carry an operation ID, a reason, the expected taxonomy revision and the expected mapping revision. A stale revision SHALL return 409 with a `detail`. A non-null category approves; null revokes.

#### Scenario: Stale mapping revision
- **WHEN** an Admin submits an approval against a mapping revision that has since changed
- **THEN** the server returns 409 with a `detail`

#### Scenario: Revoke
- **WHEN** an Admin submits a change with category null
- **THEN** the mapping is revoked and no longer applies to new saves

### Requirement: Mapping changes are kept as evidence
Every approve, change and revoke SHALL be kept as an immutable record with its actor, time, previous revision and approval revision. Reusing an operation ID with a different payload SHALL return 409 with a `detail`.

#### Scenario: Mapping history
- **WHEN** an Admin approves, changes and then revokes a mapping
- **THEN** its history shows all three changes with actor and time

### Requirement: Only explicit approval counts
Only an Admin's explicit approval SHALL create a mapping. Product links, templates, suggestions, model confidence and historical labels SHALL never count as one. Jev SHALL never create or approve a mapping.

#### Scenario: Accepting a suggestion
- **WHEN** a person accepts a Jev suggestion for an item
- **THEN** no mapping is created for that item's source

### Requirement: What keeps or breaks an approval
A rename of the target, or an edit to an unrelated category, SHALL keep an approval valid. A change to the definition or ancestry of the target or any ancestor SHALL make the mapping unavailable until re-approved. So SHALL archiving the target or any ancestor.

#### Scenario: Rename keeps it
- **WHEN** the target "Dairy" is renamed "Dairy & Eggs"
- **THEN** the mapping still applies

#### Scenario: Definition change breaks it
- **WHEN** the target's definition is edited
- **THEN** the mapping no longer applies until an Admin re-approves it

### Requirement: Reuse at save
At save, a new item with no classification intent whose exact source has a current mapping SHALL receive an `approved_mapping` assignment. Its reason SHALL name the mapping revision, the approval revision and the source fingerprint, and its actor SHALL be the person saving the receipt. An explicit choice, including a clear, SHALL win over the mapping.

#### Scenario: Explicit choice wins
- **WHEN** a person saves a new item and explicitly chooses "Snacks" though its source maps to "Dairy"
- **THEN** the item gets a `manual` "Snacks" assignment

#### Scenario: Invalid explicit choice
- **WHEN** a person saves a new item and explicitly chooses an archived category
- **THEN** the save is refused with 400 and a `detail`, and the mapping is not used instead

### Requirement: Background reuse on older purchases
A background worker SHALL apply current mappings to older purchases that are eligible for automation (see `purchase-classification`). It SHALL work in bounded pages of at most 100 purchases. It SHALL need no consent setting and SHALL never call a provider. Each of its operations SHALL belong to the approving Admin and SHALL appear in their automatic classification activity.

#### Scenario: Backfill an old purchase
- **WHEN** a mapping is approved for a source that has 3 old unresolved purchases
- **THEN** the worker gives each of them an `approved_mapping` assignment, and the operation appears in the approving Admin's activity

#### Scenario: Manual choice untouched
- **WHEN** one of those old purchases has a `manual` assignment
- **THEN** the worker does not change it

### Requirement: Approval is offered after save
The client SHALL offer **Remember category** per source to Admins in two places: next to each item on a saved receipt, and in the Classification workspace after an assistance decision. Declining SHALL leave classification unchanged. Approving SHALL never save or reclassify the receipt.

#### Scenario: Remember from a receipt
- **WHEN** an Admin uses Remember category next to a saved item
- **THEN** a mapping is approved for that item's exact source, and the receipt itself is not re-saved

#### Scenario: Skip remembering
- **WHEN** a person chooses **Skip remembering** in the Classification workspace
- **THEN** no mapping is created
