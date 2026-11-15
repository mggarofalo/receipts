# category-restructure Specification

## Purpose

Defines a category restructure. An Admin rebuilds the tree and moves past purchases onto it in one reviewed, resumable and undoable step, driven by a plan file. Purchases a person or automation decided later are left alone.

## Requirements

### Requirement: Restructure is Admin-only
Every restructure endpoint SHALL require Admin; anyone else SHALL receive 403. Any enabled Admin SHALL be able to resume, cancel or undo an application, whoever started it.

#### Scenario: Another Admin resumes
- **WHEN** one Admin's application was interrupted and a second Admin resumes it
- **THEN** it continues from where it stopped

### Requirement: Dry run reports every error and changes nothing
A preview SHALL run the same planner as apply. It SHALL report every error in the plan at once, and SHALL change nothing. Nothing SHALL change until an Admin applies the plan.

#### Scenario: Plan with three problems
- **WHEN** an Admin previews a plan with three separate errors
- **THEN** all three are reported together, and no data changes

### Requirement: Plan refusals
A plan SHALL be refused when:
- its format or version is unknown;
- the current tree's content differs from its base;
- after the plan, any active category has no definition or the tree is deeper than two levels;
- an archived category has no node mapping;
- a mapping or item names a category that is not active after the plan;
- purchases would move off an already-archived category.

Archived categories that the base lacks SHALL be ignored when comparing it.

#### Scenario: Base mismatch
- **WHEN** an Admin edits the tree after a plan was generated and then previews it
- **THEN** the plan is refused because the tree differs from its base

#### Scenario: Missing definition
- **WHEN** a plan would leave an active category without a definition
- **THEN** the plan is refused with that reason

### Requirement: Envelope references in a plan
A plan SHALL be refused in any of these cases:
- an envelope reference matches no envelope, or matches several;
- envelope merges chain;
- a merge targets an archived envelope, or defaults to one;
- merged envelopes have conflicting defaults that the plan does not resolve.

#### Scenario: Ambiguous envelope name
- **WHEN** a plan names the envelope "Food" and two envelopes match
- **THEN** the plan is refused with that reason

### Requirement: Apply then move purchases in chunks
Apply SHALL change the tree, defaults, mappings and envelope merges in one transaction. It SHALL then move purchases in resumable chunks of at most 100. Each moved purchase SHALL get a `restructure` assignment whose reason names the plan rule and its evidence.

#### Scenario: Interrupted apply
- **WHEN** the server restarts partway through moving purchases
- **THEN** the application resumes without moving any purchase twice

### Requirement: Later decisions are left alone
A purchase classified after the plan's `snapshotAt` by `manual`, `jev` or `approved_mapping` SHALL NOT be moved. It SHALL be reported as a conflict.

#### Scenario: Manual edit after snapshot
- **WHEN** a person reclassified an item after the plan's snapshot
- **THEN** apply leaves it unchanged and reports it as a conflict

### Requirement: Envelopes change only by explicit merge
A restructure SHALL NOT change any item's envelope except through an explicit envelope merge in the plan. It SHALL NOT fill unresolved envelopes either. The dry run SHALL count the items each merge moves, including how many change YNAB category.

#### Scenario: Reclassified item keeps its envelope
- **WHEN** a restructure moves an item from "Party" to "Grocery > Meat"
- **THEN** the item's envelope is unchanged

#### Scenario: Merge counted
- **WHEN** a plan merges envelope "Snacks" into "Groceries"
- **THEN** the dry run reports how many items move between those envelopes

### Requirement: Recycle-bin purchases
A purchase in the recycle bin SHALL NOT be reassigned to a new category. An envelope merge SHALL still move its envelope, and the dry run's merge counts SHALL include it.

#### Scenario: Trashed item under a merge
- **WHEN** a trashed item uses an envelope that the plan merges
- **THEN** its envelope follows the merge, and its category is not changed

### Requirement: Undo
Undo SHALL restore the tree, defaults, mappings and envelope merges. It SHALL apply the conditional-correction rule from `purchase-classification` to each moved purchase, and SHALL skip and report purchases that changed since. Undo SHALL be refused while the tree differs from the one the apply left.

#### Scenario: Tree edited after apply
- **WHEN** an Admin edits the tree after an apply and then requests undo
- **THEN** the undo is refused with a `detail`

### Requirement: One application at a time
While a restructure is applying or undoing, taxonomy edits SHALL be refused with 409 and a `detail`. Only one application SHALL be unfinished at a time.

#### Scenario: Edit during apply
- **WHEN** an Admin submits a taxonomy edit while a restructure is applying
- **THEN** the server returns 409 with a `detail`
