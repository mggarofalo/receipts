# An envelope is fixed once recorded

Date: 2026-11-15. Status: accepted; implementation tracked by RECEIPTS-1124. Tracking: RECEIPTS-1118.

## Context

Item type and envelope were separated ([Envelopes and occasions](../envelopes.md)) because they answer different
questions. Classification says what a thing is. Receipts owns it and it can change. The envelope says where the
money came from, which is the fact YNAB records.

Pushing to YNAB only creates transactions and skips any that are already synced, so Receipts never rewrites a
pushed transaction. Locally, though, every classification write re-derived the envelope of an item whose source was
`default`. That included bulk ones: cleanup campaigns and their undo, accepted assistance, and manual retypes. A
purchase pushed and reconciled months ago could therefore end up with a different envelope in Receipts than in
YNAB. Clearing an item's type also wiped its envelope, which blocked any later push of the receipt's other
transactions.

## Decision

Receipts does not rewrite history in an external system. When a correction cannot be avoided, it is targeted and a
person starts it. Inside Receipts, classification may be rewritten, because the envelope preserves what YNAB has.
The rule is the [envelope-attribution](../../openspec/specs/envelope-attribution/spec.md) spec:

- **Fixed once recorded.** A classification change, by any path, never changes a recorded envelope or its source. It
  only fills an unresolved envelope from the new type's default. A restructure fills nothing, and changes envelopes
  only through an explicit merge.
- **Only a person moves a recorded envelope,** through one of:
  - a per-item override;
  - **Automatic**, which re-derives once and records `default` or `receipt`;
  - a paid-from change;
  - an envelope merge.

  When the receipt already has a YNAB transaction, the client warns that YNAB will not change.
- **A wrong first guess** can be corrected before the first push. After a type change, the client offers "Also move
  to X?", which runs Automatic.
- **`EnvelopeSource` is provenance only.** No source makes an envelope follow later type changes.
- **Splits already in YNAB are never deleted and recreated.** Doing so would lose cleared state, the bank-import
  match and edits made in YNAB. The YNAB API does not allow an existing split's subtransactions to be changed, so a
  future targeted correction (RECEIPTS-1126) can fix only non-split transactions in place. For a split, it shows the
  expected split for a person to edit in YNAB.

## Alternatives considered

- **Freeze at first push.** Envelopes would follow type changes until the receipt was pushed. Rejected: every
  classification write would need sync state, and "where the money came from" is settled at purchase, not at push.
- **A provisional `suggested` source**, kept live until a person confirms. Rejected. Machines never retype an
  already-typed item, so only a person's later classification could move such an envelope. That move would either
  reach receipts already pushed, which is the drift this rule prevents, or reduce to freezing at push. It would also
  need a new enum value, a migration and a breaking API change.

## Consequences

- RECEIPTS-1124 implements the rule:
  - a fill-only type-change hook, with clearing a type keeping the envelope;
  - Automatic for any source;
  - the move offer;
  - warnings on pushed receipts;
  - removing the YNAB split comparison's "current hierarchy" option, which no longer predicts anything (a breaking
    API change).
- Until then, [Envelopes and occasions](../envelopes.md) describes the current behaviour and points to this
  decision.
