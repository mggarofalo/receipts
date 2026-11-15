# category-taxonomy Specification

## Purpose

Defines the single versioned tree of item types: how each category keeps a stable identity, how every edit becomes an immutable revision, and what the Admin editor at `/categories` lets a person change.

## Requirements

### Requirement: Stable category identity
Each category SHALL have a stable UUID that does not depend on its name, parent or depth. An identity SHALL never be recycled. A category that any revision, assignment or mapping references SHALL never be hard-deleted. A rename or move SHALL keep the identity, and SHALL never merge two identities.

#### Scenario: Rename keeps identity
- **WHEN** an Admin renames "Dairy" to "Dairy & Eggs"
- **THEN** the category keeps its UUID, and assignments that point to it keep pointing to it

#### Scenario: Move keeps identity
- **WHEN** an Admin moves "Snacks" from under "Grocery" to the root
- **THEN** "Snacks" keeps its UUID, and its descendants move with it

### Requirement: Immutable full-tree revisions
Each accepted change batch SHALL append exactly one new, immutable, complete revision of the tree, and SHALL advance the single current head to it atomically. A batch that changes nothing SHALL keep the current revision and report zero affected nodes.

#### Scenario: Edit creates one revision
- **WHEN** an Admin submits a batch that renames one category and moves another
- **THEN** exactly one new revision exists containing both changes, and it is the current head

#### Scenario: No-op edit
- **WHEN** an Admin submits a batch whose result equals the current tree
- **THEN** no revision is created and the response reports zero affected nodes

### Requirement: Stale-head edits are refused
A change batch SHALL carry the revision it was based on. If that revision is no longer the head, the server SHALL refuse the batch with 409 and a problem document whose `detail` gives the reason. The editor SHALL keep the person's draft until they choose to refresh.

#### Scenario: Concurrent edit
- **WHEN** two Admins edit from the same head and the second submits after the first was accepted
- **THEN** the second receives 409 with a `detail`, and their draft is still shown in the editor

### Requirement: Tree validation
The server SHALL refuse with 400 and a `detail` any batch that would create a cycle, self-parenting, a missing parent, or an empty name. It SHALL also refuse a name with leading or trailing whitespace or a NUL character, and two active siblings with the same name. Names SHALL be compared exactly, with no trimming or case-folding.

#### Scenario: Duplicate active sibling
- **WHEN** an Admin adds "Milk" under "Dairy" where an active "Milk" already exists
- **THEN** the server returns 400 with a `detail` naming the duplicate

#### Scenario: Case differs
- **WHEN** an Admin adds "milk" under "Dairy" where "Milk" exists
- **THEN** the batch is accepted, because names are compared exactly

#### Scenario: Same name under different parents
- **WHEN** "Other" exists under both "Grocery" and "Household"
- **THEN** both are valid and are shown as distinct full paths

### Requirement: Name and definition length
A category name SHALL be at most 200 Unicode scalar values. A definition SHALL be at most 4000. A longer value SHALL be refused with 400 and a `detail`.

#### Scenario: Name too long
- **WHEN** an Admin submits a name of 201 Unicode scalar values
- **THEN** the server returns 400 with a `detail`

### Requirement: At most two levels
The tree SHALL have at most two levels: root categories and their direct children. The server SHALL refuse with 400 and a `detail` any batch that would place a category below a child, either by creating it there or by moving it there. Two levels is the depth that category assistance and restructure plans support.

#### Scenario: Third level refused
- **WHEN** an Admin adds "Whole Milk" under "Grocery > Dairy"
- **THEN** the server returns 400 with a `detail`, and no revision is created

#### Scenario: Move that deepens refused
- **WHEN** an Admin moves the root "Dairy", which has children, under "Grocery"
- **THEN** the server returns 400 with a `detail`, because the children would become a third level

### Requirement: Definitions are optional
The editor SHALL accept a category with no definition, on create and on every later edit. Only restructure plans require definitions (see `category-restructure`).

#### Scenario: Create without definition
- **WHEN** an Admin creates "Pet Supplies" with no definition
- **THEN** the category is created

### Requirement: Archive, restore and move semantics
Archiving a category SHALL archive its whole subtree. Restoring SHALL act on the chosen category alone, and SHALL require its ancestors to be active. Moving a category SHALL carry its descendants. Before the change is submitted, the editor SHALL show how many categories each archive, restore or move affects.

#### Scenario: Archive a parent
- **WHEN** an Admin archives "Grocery", which has 6 children
- **THEN** the editor first shows 7 affected categories, and after confirmation all 7 are archived

#### Scenario: Restore under an archived parent
- **WHEN** an Admin restores "Dairy" while its parent "Grocery" is archived
- **THEN** the server returns 400 with a `detail`

### Requirement: Archived categories stay readable
An archived category SHALL NOT be offered for new choices. It SHALL stay visible in the editor under **Show archived**, and in every revision that contains it.

#### Scenario: Picker excludes archived
- **WHEN** a person opens a category picker after "Toys" was archived
- **THEN** "Toys" is not offered

#### Scenario: Revision detail shows archived
- **WHEN** a person views a revision in which "Toys" was archived
- **THEN** "Toys" appears in that revision, marked archived

### Requirement: Taxonomy edits never rewrite purchases
A taxonomy edit SHALL NOT change any assignment, receipt text, amount, payment or product link. An edit only changes how current reads group existing assignments.

#### Scenario: Rename leaves history alone
- **WHEN** an Admin renames "Dairy" after 50 items were classified there
- **THEN** none of the 50 items gains a new assignment

### Requirement: Taxonomy authorization
Only an Admin SHALL change the tree. Anyone else SHALL receive 403, and the rule SHALL apply equally to API keys. Any authenticated user SHALL be able to read the current revision and historical revisions, including each revision's actor, reason and time.

#### Scenario: Non-Admin edit
- **WHEN** a signed-in non-Admin submits a change batch
- **THEN** the server returns 403

#### Scenario: Read history
- **WHEN** a signed-in non-Admin requests a past revision
- **THEN** it is returned with its actor, reason and time

### Requirement: Idempotent taxonomy operations
Each change batch SHALL carry an operation ID. Retrying the same operation ID with the same body SHALL return the original result without creating another revision. Reusing an operation ID with a different body SHALL return 409 with a `detail`.

#### Scenario: Retry after timeout
- **WHEN** a client resends a batch with the same operation ID and body
- **THEN** it receives the original result, and only one revision exists

#### Scenario: Reused ID, different body
- **WHEN** a client sends a different batch under an operation ID already used
- **THEN** the server returns 409 with a `detail`

### Requirement: Legacy routes redirect
The client routes `/taxonomy` and `/subcategories` SHALL redirect to `/categories` once the person is authenticated.

#### Scenario: Old link
- **WHEN** a signed-in person opens `/subcategories`
- **THEN** they land on `/categories`
