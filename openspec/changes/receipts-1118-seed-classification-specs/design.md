# Design

## Context

See proposal.md (Why). This change writes specs, ADRs and doc fixes only. It changes no code, OpenAPI or schema, so there is no layer ownership to assign, and no migration plan.

The specs came from a two-opinion process. Two independent decomposition proposals were combined, then their load-bearing claims were checked against the code. The envelope rule went through a separate two-opinion stress test against the code. The owner answered every open question. The answers, and the defaults the owner accepted, are listed under Decisions.

## Goals / Non-Goals

**Goals:**
- One requirement for each behaviour. Rules shared by several writers are stated once and referenced.
- Every code-versus-intent gap is listed here and tracked as a Plane issue, not hidden in a spec.
- The docs agree with the specs when this change merges.

**Non-Goals:**
- Making the code meet the specs. The follow-up issues listed below do that.
- Specifying implementation details, such as lock order, triggers, query plans, transport and evaluation numbers.

## Decisions

### Capability boundaries
- **Shared rules live in `purchase-classification`.** That covers automation eligibility and conditional correction. Writer specs refer to them rather than restate them, because the mapping worker, Jev, cleanup, activity undo and restructure undo all use the same rules. *Alternative:* restate the rules in each writer's spec. Rejected, because it repeats one piece of knowledge four times.
- **`automatic-classification-activity` is its own capability**, not part of cleanup. It has its own page (the Classification workspace), its own attribution rules, its own producers and its own suppression rule. Its code shares the cleanup engine, but that is an implementation detail.
- **The Uncategorized items view is split by whether it needs Jev.** The list and the manual fix are in `purchase-classification`, because they work without a provider. Jev's answer column is in `category-assistance`.
- **`envelope-attribution` is added now.** The owner decided the envelope rule during this planning, and the classification specs depend on it.

### Open questions and the owner's answers
| # | Decision | Spec |
|---|---|---|
| Q1 | Jev strong-match automatic writes are intended. A new ADR supersedes the automatic-action gate in the 2026-10-28 ADR. | category-assistance |
| Q2 | The tree has at most two levels. The editor enforces it. | category-taxonomy |
| Q3 | Definitions are optional in the editor. Only restructure requires them. | category-taxonomy, category-restructure |
| Q4 | Stored legacy labels do not block automation. | purchase-classification |
| Q5 | Restructure-cleared items are eligible for automation. | purchase-classification |
| Q6 | Historical cleanup and Jev historical requests are Admin-only. | historical-reclassification, category-assistance |
| Q7 | Undo writes `manual`. That is the intended suppression; a hand choice reopens the item. | automatic-classification-activity |
| Q8 | Six sources. Accepted suggestions, cleanup applies and undos are recorded as `manual`. | purchase-classification |
| Q9 | Selecting a cleanup group counts as explicit inclusion, so `manual` items in it are reclassified. | historical-reclassification |
| Q10 | Top category follows the dashboard's view. | category-reporting |
| Q11 | Mapping approval stays a post-save action for the Admin. Remembering at entry is a separate idea, to revisit after testing. | approved-category-mappings |
| Q12 | The per-source Remember panel is enough. No page listing every mapping. | approved-category-mappings |
| Q13 | Later text wins. The stale lines are fixed in this change. | docs only |

### The envelope rule
An envelope is fixed once recorded. Classification only fills an unresolved envelope, and only a person moves a recorded one (see `envelope-attribution`).

- **Rejected: freezing at first push.** It would need sync state wherever classification is written, and the owner prefers "fixed when recorded".
- **Rejected: a provisional `suggested` source that stays live until a person confirms.** No machine ever retypes an already-typed item, so only a person's later classification could move such an envelope. Either that move would happen on receipts already pushed, recreating the drift, or the idea reduces to freezing at push. It would also need a new enum value, a migration and a breaking API change.
- **Kept: the "Also move to X?" offer.** It fixes a wrong first type on a receipt that was never pushed. Accepting records source `default` through the Automatic action, not an `item` override, so the provenance stays honest and a later paid-from change still applies.
- **The split comparison's "Preview current hierarchy" option will be removed** in the implementation change. Under this rule it no longer predicts anything. Removing it is breaking, so that change needs the label and a `!` in its title.

### ADRs
- `docs/decisions/2026-11-15-jev-strong-match-automatic-writes.md` supersedes ADR 2026-10-28 on automatic Jev actions. It records that the owner accepted pilot thresholds: root-only writes at fit ≥ 0.7 and root ≥ 0.90, with leaf writes off. It also records that undo-as-`manual` is the suppression, and that the thresholds are re-evaluated on the restructured tree.
- `docs/decisions/2026-11-15-envelope-fixed-once-recorded.md` records the envelope rule, the alternatives above, and that Receipts never rewrites YNAB history. When a correction is unavoidable, it is targeted and started by a person, and a split already in YNAB is never deleted and recreated.

## Risks / Trade-offs

- **[The specs state behaviour the code does not have yet, so manual tests of it will fail.]** → The gaps are listed below, each tracked by a Plane issue. A failure in a listed gap is expected. A failure anywhere else is a real finding.
- **[Archiving moves intended behaviour into `openspec/specs/` before the code meets it.]** → Specs are the answer key by design. The gaps stay tracked in Plane until each is fixed.
- **[The owner's answers may change after testing.]** → Revise through a normal OpenSpec change that modifies the requirement. Don't edit an archived spec silently.

### Known code gaps (follow-up issues)
| Gap | Spec requirement | Issue |
|---|---|---|
| The editor has no depth limit | category-taxonomy: At most two levels | RECEIPTS-1119 |
| Stored legacy labels block automation | purchase-classification: Stored legacy labels do not affect eligibility | RECEIPTS-1120 |
| Restructure-cleared items are skipped by Jev and the mapping worker | purchase-classification: Automation eligibility | RECEIPTS-1121 |
| Cleanup and Jev historical-request endpoints only require sign-in | historical-reclassification: Cleanup is Admin-only; category-assistance: Bounded historical requests | RECEIPTS-1122 |
| The dashboard's Top category ignores the view | category-reporting: One dashboard, one view | RECEIPTS-1123 |
| A type change re-derives `default` envelopes, clearing a type wipes the envelope, there is no Automatic for every source, no move offer, no pushed-receipt warnings, and the stale split preview remains | envelope-attribution (all) | RECEIPTS-1124 |

Ideas filed: RECEIPTS-1125 (remember a mapping at entry) and RECEIPTS-1126 (targeted correction of one non-split YNAB transaction).
