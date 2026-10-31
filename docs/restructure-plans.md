# Category restructure plans (RECEIPTS-1084)

A restructure plan changes the category tree and moves past purchases onto the new tree in one
reviewed step. Use it when the tree itself is wrong, for example when top-level categories were
used as budget envelopes rather than item types. For one-off fixes, use the category editor and
[historical reclassification](historical-reclassification.md) instead.

A plan is a JSON file generated outside the app from a backup. An administrator uploads it under
**Admin → Restructure Categories**. The server checks it and shows a dry run. Nothing changes
until the administrator applies it, and the whole application can be undone.

## What a plan contains

```json
{
  "format": "receipts-restructure-plan",
  "version": 1,
  "planId": "…",
  "reason": "Item types, not envelopes",
  "baseTaxonomyRevisionId": "…",
  "snapshotAt": "2026-10-29T18:54:28Z",
  "taxonomyChanges": [{ "kind": "rename", "categoryId": "…", "name": "Medical" }],
  "nodeMappings": [{ "categoryId": "…", "targetCategoryId": "…", "occasion": "Easter" }],
  "items": [{ "itemId": "…", "targetCategoryId": "…", "rule": "d", "evidence": { } }],
  "occasions": ["Easter"],
  "envelopeDefaults": [{ "categoryId": "…", "envelope": { "name": "Groceries" } }],
  "envelopeMerges": [{ "source": { "name": "Medical (2)" }, "target": { "name": "Medical" } }]
}
```

- **`taxonomyChanges`** are the same edits the category editor makes: `create`, `rename`,
  `move`, `define`, `archive` and `restore`. A merge is an archive plus a node mapping onto the
  surviving category. Surviving categories keep their IDs, so approved mappings and reports keep
  working.
- **`nodeMappings`** say where purchases on an old category go: to a new category, or cleared
  (`"clear": true`). Every category the plan archives needs one. An optional `occasion` tags the
  moved purchases, so an occasion category such as "Easter" can become a tag.
- **`items`** override the node mapping for single purchases. Each carries the rule that decided it
  and that rule's inputs as `evidence`. The evidence is stored on the new assignment.
- **`envelopeDefaults`** set or remove an item type's default envelope. Envelopes are named, not
  referenced by ID, because IDs differ per deployment. Add `ynabCategoryId` when two envelopes
  share a name.
- **`envelopeMerges`** move every purchase, receipt and default from one envelope to another, then
  archive the source.

## What the server checks

The dry run (`POST /api/restructure-plans/preview`) runs the same planner as apply and reports
every error at once. A plan is refused when:

- the format or version is unknown;
- the category tree no longer matches the plan's base tree (compared by content, so a plan that
  was undone can be applied again);
- an active category has no definition, or the tree is deeper than two levels;
- an archived category has no node mapping, or a mapping or item names a category that is not
  active after the plan;
- an envelope reference matches no envelope, or more than one;
- a merge chains into another merge or targets an archived envelope, or a default names an
  archived envelope;
- categories merged into another bring a default envelope that differs from the one it has or
  inherits, and the plan does not say which wins.

A plan never overwrites a person's later decision. A purchase classified after `snapshotAt` by
hand, by Jev or by an approved mapping is left as it is and reported as a conflict.

## Budget parity

A restructure describes what a purchase is, not who paid for it. Purchases keep the envelope they
have, so their YNAB category does not change. The one exception is an explicit envelope merge,
which the dry run reports as a count of purchases changing envelope and YNAB category.

## Apply and undo

`POST /api/restructure-plans` with an `applicationId` and the plan changes the tree, defaults,
mappings, occasions and envelope merges in one transaction, and captures every purchase to move.
The purchases then move in chunks of at most 100 through `POST /api/restructure-plans/{id}/apply`.
The page repeats that call until nothing is pending. An interrupted run keeps every committed chunk
and resumes from the list.

Each moved purchase gets a `restructure` assignment whose reason is the rule's evidence. Its
envelope is not touched.

`POST /api/restructure-plans/{id}/undo` puts everything back: the tree, defaults, mappings,
envelope merges and each purchase. Undo skips anything someone changed since, and reports it. It
is refused once the category tree has changed after the apply, because that change would be lost.
Undoing an application that was still running cancels it first.

A plan ID can be applied again only after its previous application is undone, and only one
application can be unfinished at a time. Every endpoint requires the admin role.

## Backups

Restructure assignments are ordinary classification history and are in portable backups. The
application records, their ledger and the `RestructureV1` operation are local to the deployment
and are not exported, so a restored deployment cannot undo a restructure applied before the
backup.
