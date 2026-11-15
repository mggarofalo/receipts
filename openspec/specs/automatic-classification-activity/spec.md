# automatic-classification-activity Specification

## Purpose

Makes every automatic classification visible and reversible. Whatever the approved-mapping worker or Jev wrote is listed for the responsible person, who can undo it without any provider.

## Requirements

### Requirement: Every automatic write has an activity entry
Every assignment written by the approved-mapping worker or by Jev SHALL belong to an activity entry. A mapping entry SHALL be attributed to the approving Admin. A Jev entry SHALL be attributed to the person whose save produced the request.

#### Scenario: Mapping backfill
- **WHEN** the mapping worker classifies 12 old purchases under a mapping an Admin approved
- **THEN** one entry in that Admin's activity accounts for all 12

### Requirement: Activity list content
The Classification workspace SHALL show the signed-in person's **Automatic classification activity**. Each entry SHALL show the descriptions and stores affected, the category paths written, counts, and the source (`approved_mapping` or `jev`).

#### Scenario: View activity
- **WHEN** a person opens the Classification workspace after Jev wrote strong matches from their saves
- **THEN** they see an entry with source `jev`, the items' descriptions and stores, the paths and the count

### Requirement: Entries record committed work
An activity entry SHALL describe writes that have already been committed. It SHALL NOT be a preview awaiting acceptance.

#### Scenario: Nothing to accept
- **WHEN** an entry appears in the activity list
- **THEN** its assignments are already current, and nothing needs approving for them to take effect

### Requirement: Undo uses conditional correction
Undoing an entry SHALL apply the conditional-correction rule from `purchase-classification`: only items whose current assignment is still the one this entry wrote are reverted. It SHALL run in bounded chunks with a fixed audit reason, and report applied, conflicted and skipped counts.

#### Scenario: Partly changed since
- **WHEN** an entry wrote 10 items, a person later changed 2 of them by hand, and the entry is undone
- **THEN** 8 items are reverted and 2 are reported conflicted

### Requirement: Undo refuses a changed taxonomy
If the taxonomy head changed between loading the activity and the first undo chunk, the undo SHALL return 409 with the `detail` "Categories changed. Try Undo again." It SHALL leave no frozen partial state behind.

#### Scenario: Categories changed
- **WHEN** an Admin edits the tree after a person loaded the activity, and the person then clicks Undo
- **THEN** the server returns 409 with that `detail`, and the person can retry

### Requirement: Undo suppresses the same decision
Undo SHALL write a `manual` value, usually null, so automation does not write the same item again. Only a person choosing a type by hand SHALL reopen the item. This is the intended suppression: undone items stay out of every automatic writer, including restructure.

#### Scenario: Not re-applied
- **WHEN** a mapping entry is undone and the mapping remains approved
- **THEN** the worker does not reclassify the reverted items on its next pass

### Requirement: Works without a provider
The activity list and undo SHALL work when no assistance provider is configured, enabled or reachable.

#### Scenario: Jev disabled
- **WHEN** an Admin turns assistance off and a person undoes an earlier `jev` entry
- **THEN** the undo proceeds normally

### Requirement: Jev entries support undo only
In this list, a Jev entry SHALL support undo only. It SHALL NOT be re-applied or cancelled from here.

#### Scenario: No re-apply
- **WHEN** a person views an undone `jev` entry
- **THEN** no action is offered to apply it again
