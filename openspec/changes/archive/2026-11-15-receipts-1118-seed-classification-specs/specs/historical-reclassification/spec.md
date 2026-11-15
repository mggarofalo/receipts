# Spec Delta

## Purpose

Defines cleanup campaigns. An Admin reclassifies many past purchases at once by exact source group, through a frozen server-built preview, a chunked apply that can be cancelled, and a conditional undo. This is the cleanup page at `/categories/cleanup`.

## ADDED Requirements

### Requirement: Cleanup is Admin-only
Every cleanup endpoint SHALL require Admin. Anyone else, including an API key without Admin, SHALL receive 403.

#### Scenario: Non-Admin preview
- **WHEN** a signed-in non-Admin requests a cleanup preview
- **THEN** the server returns 403

### Requirement: Server-built preview from exact groups
The server SHALL build a preview from 1 to 25 explicitly selected exact source groups, each with an explicit target category. A group is a store, a raw description and an optional code, compared exactly. The client SHALL NOT supply a list of items. A group with no available items SHALL be refused with 400 and a `detail`.

#### Scenario: Valid preview
- **WHEN** an Admin selects 3 groups, each with a target
- **THEN** the server returns a preview listing each group's items and counts

#### Scenario: Too many groups
- **WHEN** an Admin selects 26 groups
- **THEN** the server returns 400 with a `detail`

### Requirement: The preview freezes its scope
A preview SHALL freeze:
- the actor, operation ID, reason and taxonomy head;
- the group keys;
- each item's source and receipt revisions, and its expected current assignment.

Apply SHALL act only on that frozen set. It SHALL never pick up items saved after the preview.

#### Scenario: Item added after preview
- **WHEN** a new purchase from a selected group is saved after the preview
- **THEN** apply does not change it

### Requirement: Selecting a group is explicit inclusion
Selecting a group SHALL count as explicitly including every item in it. Each item SHALL be reclassified to the target whatever its current source, `manual` included.

#### Scenario: Manual item in group
- **WHEN** a selected group contains an item a person classified by hand
- **THEN** apply reclassifies it to the group's target

### Requirement: Chunked apply and cancel
Apply SHALL advance at most 100 pending items per call, in a persisted order. It SHALL commit each item's assignment and outcome together. Cancel SHALL stop future chunks, and SHALL NOT reverse chunks already committed.

#### Scenario: Cancel midway
- **WHEN** an Admin cancels after 2 of 5 chunks were applied
- **THEN** the items from those 2 chunks keep their new assignments, and no further items change

### Requirement: Changed items become conflicts
An item whose source, assignment or deletion state changed after the preview SHALL be recorded as a durable conflict or skip, never overwritten. So SHALL an item affected by a change to the taxonomy head. An item whose current category already equals the target SHALL be skipped.

#### Scenario: Edited after preview
- **WHEN** a person edits an item's description after it was previewed
- **THEN** apply records that item as a conflict and leaves it unchanged

### Requirement: Durable counts
Progress counts SHALL be derived from each item's recorded outcome: applied, conflicted, skipped and pending. Undo SHALL have its own matching counts.

#### Scenario: Reload mid-run
- **WHEN** an Admin reloads the page partway through an apply
- **THEN** the counts shown match the outcomes recorded so far

### Requirement: Replay by operation ID
Repeating a request with the same operation ID and intent SHALL return the progress already recorded. A changed intent under the same operation ID SHALL return 409 with a `detail`.

#### Scenario: Replay
- **WHEN** an Admin resends an apply with the same operation ID and intent
- **THEN** recorded progress is returned and no item is changed twice

### Requirement: Undo uses conditional correction
Undo SHALL apply the conditional-correction rule from `purchase-classification`. The first undo chunk SHALL freeze the undo's intent: its taxonomy revision and reason. A later chunk with a different intent SHALL return 409 with a `detail`.

#### Scenario: Undo after later edit
- **WHEN** a campaign is undone after a person changed one of its items
- **THEN** that item is reported conflicted and the rest are reverted

### Requirement: Group dispositions are private
An Admin SHALL be able to mark a group **Later** or **Leave as is**. A disposition SHALL be private to that person, SHALL hide the group from their default list, and SHALL never be used as classification evidence.

#### Scenario: Leave as is
- **WHEN** an Admin marks a group Leave as is
- **THEN** it disappears from their default group list, and no item in it changes

### Requirement: No provider needed
Cleanup campaigns SHALL work without any assistance provider. Each operation SHALL belong to the Admin who created it.

#### Scenario: Jev not configured
- **WHEN** no assistance provider is configured
- **THEN** preview, apply, cancel and undo all work
