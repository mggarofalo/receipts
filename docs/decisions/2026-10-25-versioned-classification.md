# Versioned categories and purchase classification

Date: 2026-10-25. Status: design decision; storage and migration are not implemented by this ADR.
Tracking: RECEIPTS-1044 in [Versioned Categories & Assisted Classification](https://plane.wallingford.me/dev/projects/aaac8dc9-bc4c-42db-ac99-eee7864c78e9/modules/08017dad-c99e-49a5-90aa-4d091ddb8353).

## Decision

Use one category identity with an optional single parent. Category, subcategory,
and type are positions in a tree, not different entity kinds. For example,
`Grocery > Dairy > Milk` describes a purchase without claiming that all milk
purchases are the same product. A purchase may use any active node, including a
broad parent, or remain unclassified. Saving a receipt never requires provider
availability or resolving every item.

Keep raw receipt text, product code, brand/size/variety evidence, readable labels,
and exact product identity separate from classification. Existing normalized
descriptions are not automatically category leaves. This revises the direction
of the classification UX without silently changing the existing normalization
catalog's identity, price-history, and approved-mapping contracts.

Use stable category IDs, immutable full-tree revisions, and a separate append-only
classification history per receipt item. Each classification pins its taxonomy
revision. Versioning only the assigned leaf is insufficient: moving its parent
must not rewrite the path originally selected. Receipt purchase date and the time
an assignment was recorded are different facts. Effective-date scheduling and
multiple parents are deferred.

Reports explicitly choose **as classified** or **current hierarchy**. Changing
the report view never writes classifications. Retrospective reclassification is
an explicit, scoped operation with preview, concurrency checks, provenance, and
conditional undo. History is preserved by appending a correction, not editing or
deleting old assignments.

The [contract and migration plan](../classification-contract.md) defines the
invariants, examples, API work, compatibility boundaries, and scenario owners.
Its endpoint tables are plans for subsequent spec-first PRs, not claims that
those routes already exist.

## External services

Application owns candidate selection, classification policy, acceptance rules,
capability semantics, and persistence decisions. Infrastructure owns Jev HTTP,
authentication, wire schemas, timeout/error translation, and provider identifiers.
Neither Domain nor receipt saving depends on Jev. Provider responses are proposals,
never authority to write history. The first integration exposes only an
application-owned connection/capability port; inference follows a separate
evaluation and adapter decision (RECEIPTS-1056/1057).

Manual classification and approved exact mappings work without a provider. The
UI uses server-provided capabilities, not the presence of a browser key or a
hardcoded model name. Jev-dependent affordances are absent when configuration is
missing, disabled, loading, or unknown. Admin setup/status remains discoverable.
Configured credentials alone do not mean validated credentials, a reachable
provider, an accepted model, or enabled automatic classification. These conditions
must not be collapsed into one optimistic `enabled` flag.

## Tradeoffs

Full-tree snapshots use more storage than leaf-only versions but make historical
paths unambiguous and restore self-contained. Start with relational snapshot rows
and a materialized current-head projection; optimize physical storage only while
retaining logical immutability. Tree edits are much less frequent than purchases.
No revision is created merely because a receipt is saved.

Keeping compatibility labels temporarily adds write obligations. Removing them
immediately would break imports, reports, integrations, and older clients. The
rollout therefore requires validated dual writes before switching reads and an
explicit retirement gate. Missing legacy history remains missing; migration must
not invent old hierarchy changes from today's suggestion names.
