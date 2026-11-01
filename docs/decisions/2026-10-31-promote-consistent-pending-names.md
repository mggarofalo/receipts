# Promote consistent pending names instead of showing unreviewed ones

Date: 2026-10-31. Status: accepted and implemented; applying it to production awaits a local upgrade rehearsal. Tracking: RECEIPTS-1107.

## Context

Catalog retrieval (`NormalizationCandidateRetrieval`) returns only Active names. On the owner's
data, 1,420 of 1,649 catalog names are Pending review. 5,166 source keys with a legacy link point at
a pending name, against 671 at an active one. For most purchases the right name cannot appear in any shortlist. That
limits recall more than any retrieval change (RECEIPTS-1094, 1095, 1097, 1102). The owner will not
review names one by one.

Two options were on the table:

- **Show** Pending review names in the chooser and catalog shortlist, marked as unreviewed.
- **Promote** names that show consistent use, as one auditable, undoable bulk action.

## Evidence

Measured on a local instance restored from the owner's backup of 2026-10-29. Figures are aggregates.

- **Pending review records an old similarity band, not a judgement.** The retired resolver created
  a Pending review name when an existing name sat between the pending and auto-accept thresholds
  (0.68 and 0.81, in the previous embedding model). Nothing has created one since. No human
  reviewed any of the 1,420.
- **Pending names look like Active names.** In both statuses, every name is a description
  from a linked purchase (1,420 of 1,420 pending, 228 of 229 active), and nearly all are mixed case.
- **Similarity cannot find the duplicates.** With current embeddings, only 270 pending names have a
  neighbour at 0.80 or above, and only 2 at 0.82 or above. A sample at 0.78 to 0.81 was mostly
  distinct products, and the clearest duplicate pair sat below 0.80. An embedding cut would hold back
  about half the purchases while missing the duplicates it was meant to catch.
- **Links are consistent.** Of 5,834 source keys, 5,794 point at one name. 1,364 pending names have
  no source key shared with another name.
- **Showing unreviewed names gives nothing selectable.** Every write path requires an Active target:
  scoped mappings, display labels and receipt choices. A write that targets a pending name is
  refused, and proposing a new name that a pending name already holds returns
  `pending_identity_requires_review`. Showing them would only add a per-name approval step, which the
  owner will not do, or would mean relaxing every write path to accept unreviewed identities.

The evidence supports promotion, so this record does not stop the work.

## Decision

An administrator can promote, in one action, every Pending review name that meets all of these:

1. **At least 2 live purchases.** A second purchase under the same name is the first evidence that
   it is reused. 547 names have only one purchase and stay pending.
2. **No conflicting source key.** None of its purchases' source keys (store, description, item code)
   is also linked to another name.
3. **No name collision.** No other name has the same letters and digits, ignoring case and
   punctuation.

On the restored data, 816 names qualify, covering 11,584 purchases and 3,899 source keys.

The action is previewed first: the count, the purchases and the list of names. It applies only if
the eligible set has not changed since the preview. It records one promotion with the actor, the
time, and each name with its revision just after the promotion. One audit entry records the
operation, and each status change is audited automatically, as for the pending-name requeue.

Undo returns a name to Pending review only while it is still Active and unchanged since the
promotion. A name stays Active, and is reported as kept, if since then it was:

- renamed, or otherwise given a new revision;
- merged away or deleted;
- rejected;
- given an approved mapping;
- linked to a verified purchase. Returning its name to review would reset that review decision.

The promotion history is an operation log, so portable backups leave it out, as they leave out the
audit log. After a restore, promoted names stay Active, and their promotion can no longer be undone.

## Interaction with "never approve identities automatically"

`docs/chatgpt-normalization.md` says navigation and model suggestions never approve identities
automatically, and RECEIPTS-1093 decided that strong Jev matches may not apply automatically
([decision](2026-11-01-no-automatic-jev-catalog-matches.md)).
Promotion is neither. A signed-in administrator starts it after reading a preview, and it uses only
deterministic facts about the owner's own links. It never runs in the background or on a model's
advice.

It approves names, not purchases. Purchases on promoted names keep their link and their
`NeedsReview` state. No scoped mapping is created, so new purchases still resolve only through
reviewed exact mappings.

## Consequences

- Promoted names become reachable by catalog retrieval, the receipt chooser, labels and mappings.
- `NormalizationAiDecisionPolicy.IsEligibleSource` treats a source on a pending name as unassigned.
  Its only caller is the offline ChatGPT evaluation command, so promotion changes no product
  behaviour there.
- Duplicates that already exist stay as two Active names until someone merges them. Promotion
  neither creates nor merges names.
- Names with a single purchase, a conflicting key or a collision stay pending and keep the existing
  review route.
- The recall proxy (legacy links, not human-approved) gains the promoted names' source keys, so its
  coverage should rise from 668 qualifying sources.
