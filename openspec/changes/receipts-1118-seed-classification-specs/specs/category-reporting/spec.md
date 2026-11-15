# Spec Delta

## Purpose

Defines how reports read spending by item type, in either of two explicit views ("as classified" or "current hierarchy"), and how null, unresolved and pre-migration items are counted. Reading a report never changes a classification.

## ADDED Requirements

### Requirement: Every category report states its view
Every category report SHALL use a view: `asClassified` (the default) or `currentHierarchy`. It SHALL return its view, rollup, knowledge cutoff and captured taxonomy revision in its metadata. An unknown view value sent to the API SHALL be refused with 400 and a `detail`.

#### Scenario: Default view
- **WHEN** a client requests category spending without a view
- **THEN** the report uses `asClassified` and says so in its metadata

#### Scenario: Unknown view
- **WHEN** a client requests `categoryView=latest`
- **THEN** the server returns 400 with a `detail`

### Requirement: As classified uses the recorded path
The `asClassified` view SHALL group each item by the path recorded on its assignment, in that assignment's pinned revision. A later rename or move SHALL NOT change where an item appears in this view.

#### Scenario: Moved category
- **WHEN** "Snacks" moved from "Grocery" to the root after items were classified under "Grocery > Snacks"
- **THEN** those items still report under "Grocery > Snacks" in `asClassified`

### Requirement: Current hierarchy regroups in one captured revision
The `currentHierarchy` view SHALL read the same assignments, and SHALL group their category IDs by one captured current revision, which it SHALL return. An archived category SHALL stay visible under its current path.

#### Scenario: Moved category, current view
- **WHEN** the same items are reported in `currentHierarchy`
- **THEN** they report under the root "Snacks", and the response names the revision used

### Requirement: Buckets keyed by identity
Report buckets SHALL be keyed by revision and category IDs, never by display name. Equal names under different parents SHALL be distinct buckets. A renamed path SHALL stay a distinct bucket from its new name.

#### Scenario: Same name, different parents
- **WHEN** items are classified under both "Grocery > Other" and "Household > Other"
- **THEN** the report shows two separate "Other" buckets

### Requirement: Rollup counts each amount once
A parent's total SHALL include amounts assigned directly to the parent plus amounts assigned to its descendants. Each amount SHALL be counted exactly once.

#### Scenario: Parent with direct and child assignments
- **WHEN** $10 is assigned to "Grocery" and $5 to "Grocery > Dairy"
- **THEN** "Grocery" totals $15 and "Grocery > Dairy" totals $5

### Requirement: Knowledge cutoff
With `knowledgeCutoff`, a report SHALL use, for each item, the latest assignment recorded at or before the cutoff, with ties broken by sequence. It SHALL NOT invent history from before the migration boundary; those items SHALL report as `BeforeMigrationHistory`. The cutoff SHALL affect classification only. Amounts SHALL stay current.

#### Scenario: Cutoff before a correction
- **WHEN** an item was "Dairy" on Nov 1 and corrected to "Bakery" on Nov 10, and the cutoff is Nov 5
- **THEN** the item reports under "Dairy"

#### Scenario: Cutoff before migration
- **WHEN** the cutoff is earlier than the migration's first recorded observation for an item
- **THEN** the item reports as `BeforeMigrationHistory`

### Requirement: Untyped states are distinct
These SHALL be distinct report states: an explicit null, a missing assignment, an unresolved legacy path, and a legacy category named `Uncategorized`. Unfiltered totals SHALL include unresolved amounts. A category filter SHALL exclude them.

#### Scenario: Unfiltered total
- **WHEN** a report covers $100, of which $20 is unresolved
- **THEN** the report's total is $100 and the unresolved $20 is its own state

### Requirement: Currencies are never summed
A report SHALL NOT add amounts in different currencies together. Top N and Other SHALL be chosen separately for each currency. Other SHALL carry no taxonomy identity.

#### Scenario: Two currencies
- **WHEN** the period contains USD and CAD purchases
- **THEN** each currency has its own totals and its own Top N and Other

### Requirement: Period and scope
A report period SHALL select by receipt date, with both bounds inclusive. Receipts and items in the recycle bin SHALL be excluded.

#### Scenario: Trashed receipt
- **WHEN** a receipt in the period is in the recycle bin
- **THEN** its items do not appear in the report

### Requirement: Selected view travels in the URL
The client's selected view SHALL be stored in the `categoryView` URL parameter, so it survives reloads and shared links. A missing or invalid value SHALL fall back to `asClassified`. A locally stored preference SHALL NOT override the URL.

#### Scenario: Shared link
- **WHEN** a person opens a report link with `categoryView=currentHierarchy`
- **THEN** the report opens in `currentHierarchy`

### Requirement: One dashboard, one view
On the dashboard, every category figure SHALL follow the page's selected view. That includes spending by category and **Top category**.

#### Scenario: Top category follows the view
- **WHEN** a person switches the dashboard to `currentHierarchy`
- **THEN** both the spending-by-category chart and Top category are computed in `currentHierarchy`

### Requirement: Reporting never writes
Switching a view, setting a cutoff or applying a filter SHALL NOT write any assignment or other classification data. Envelope and occasion filters SHALL only narrow which items are counted.

#### Scenario: Switch view
- **WHEN** a person switches a report between views
- **THEN** no assignment is appended
