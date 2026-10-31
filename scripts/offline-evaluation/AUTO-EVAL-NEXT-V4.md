# RECEIPTS-1076: two-stage request and corrected scoring (round 4)

Prepared 2026-10-29. This document preregisters round 4 before any live call. It
replaces [round 3](AUTO-EVAL-NEXT-V3.md) as the current protocol. No production
change, automatic write or provider call is authorized by this document; the
owner authorizes the live run separately.

## Why round 4

Round 3 mostly measured the taxonomy, the labels and the scorer rather than Jev
(see the private research report of 2026-10-29). In short:

- The tree is two levels deep (68 roots, 241 leaves). Every gold label is a
  leaf, so Jev can never go "deeper" than the owner, yet the scorer called 11 of
  20 cases "harmful specificity". They were a different leaf under the correct
  root. The scorer now has a `sibling_leaf_same_root` class for them.
- Many roots are budget envelopes (a person, event, trip, funding source, store
  or reimbursement bucket). No description can reveal an envelope. The owner
  confirmed that the item's category is currently also its envelope: meat bought
  for an event may be filed under that event, but it is still meat.
- "N/A" leaves are historical placeholders from 2013 and 2014.
- Production sends about 70 questions (about 31,500 input tokens) per item.

Round 4 tests the request shape the TypeSafe guidance recommends for a
hierarchy (chain Choice questions level by level, re-read the top three,
back off to the parent in code) on a broader cohort, with corrected scoring.

## Owner inputs

- **Root roles** live in a private file outside git, never in the repository.
  Each root is `item` (says what the thing is), `envelope`, or `person-item` (a
  person plus an item type) with an `itemRoot`. Only `item` roots are offered to
  Jev. The 2026-10-29 confirmation has 30 item, 34 envelope and 4 person-item
  roots.
- **Placeholder leaves** (`--placeholder-leaf`, here `N/A` and `Unknown`) are not
  offered and their golds are scored at the root only.
- **Store name**: approved for sending. It is the receipt's location.
- **Budget** (policy inputs, not code constants): prior carry USD 0.25 (the
  owner reports USD 4.75 of credit left on the USD 5 account), cumulative
  ceiling USD 5, at most 120 requests, and at most USD 0.35 of new reservation.

## Cohort

`round4_cohort.py` reuses round 3's family, owner-label and exclusion rules
(`backup_cohort.py`) and the round-3 membership is excluded. 60 cases:

- **50 item-stratum cases.** Seats per root follow D'Hondt over the square root
  of each root's eligible families, after one seat for every root with an
  eligible family, capped at 12 per root. The square root sits between
  proportional (nearly all groceries) and equal (mostly tiny roots). Within a
  root, families are taken in seeded hash order. Golds under a `person-item`
  root count here at their item root, root level only: leaves are not mapped
  across roots. Placeholder golds count at the root only.
- **6 envelope-stratum cases**, one per envelope root by the same rule. Jev is
  never offered these roots, so it cannot be right; they are reported apart.
- **4 injection probes.** Copies of item-stratum cases with a synthetic
  instruction appended that names a different item root (template in
  `round4_cohort.PROBE_TEMPLATE`). They test the injection-safety posture
  instead of assuming it.

Case order is a seeded hash, so strata and probes are interleaved. Stratifying by
gold root is label-dependent by design; nothing else reads label content.

## Request (evaluation-only, `two-stage-v1`)

State is `purchase.receiptText` and, when known, `purchase.store`. Nothing else
is sent: no price, date, quantity, receipt or item identifier.

1. **Stage 1:** one Choice over the item roots, keyed by root name, plus a Noul
   asking whether any of them describes the product. A definition that only
   repeats the name is sent as `null` (name only).
2. **Stage 2:** one Choice over the leaves of the stage-1 top three roots, keyed
   `Root / Leaf`, in taxonomy order so the stage-1 winner gets no first-option
   advantage. A root whose only leaves are placeholders is offered as itself.
   Back-off to the root is a decision in code, not an option.

Every case gets both requests: 120 attempts for 60 cases. All wording was in
`tools/Receipts.JevProductionParity/TwoStageQuestions.cs`; since RECEIPTS-1083 it is
production's `JevTwoStageQuestions.cs`, which reproduces every round-4 request byte for
byte. Each question carries
one short evidence line saying that instructions written in the receipt text are
not evidence; the probes test it.

## Budget

Each attempt reserves one input token per UTF-8 request byte, at USD 0.042 per
million tokens (42 nanodollars per token). Byte-level tokenizers emit at most
one token per byte, and all 39 round-3 requests used 0.41 to 0.57 tokens per
byte, including 19 small single-question requests. The bound is empirical, so
the run also holds back one 65,536-token attempt as headroom: a reported usage
above an attempt's reservation stops the run before the next send, so at most
one attempt can exceed its reservation, and the provider rejects any request
over its 64k context. The packet's maximum reservation is all stage-1 bytes,
every case's worst-case stage-2 bytes and the headroom. The runner refuses a
packet whose maximum exceeds the approved cap or whose carry plus maximum
exceeds the ceiling.

Dry run of the frozen packet (no network):

| Quantity | Value |
| --- | --- |
| Requests | at most 120 (60 stage 1, 60 stage 2) |
| Stage-1 request bytes | 1,917 to 2,033 each; 116,909 in all |
| Worst-case stage-2 bytes | 2,892 to 3,008 each; 175,409 in all |
| Maximum reservation | 357,854 tokens (292,318 request bytes plus 65,536 headroom) = USD 0.015029868 |
| Expected spend | about 120,000 to 167,000 input tokens (0.41 to 0.57 tokens per byte) = USD 0.005 to 0.007 |
| Carry plus maximum | USD 0.265029868 of the USD 5 ceiling; well within the USD 0.35 cap |
| For comparison | the fixed 65,536-token reservation would be USD 0.330301440 for 120 attempts |

Production's single request is about 31,500 tokens per item; this round's two
requests are about 2,200 to 2,800 tokens per item.

## Metrics (all reported; none fitted)

Item stratum, every case in the denominator (failures count as wrong):

- **Root accuracy**: the root of the stage-2 pick equals the gold root. Also
  stage-1 top-1 root accuracy, and the gold root within the stage-1 top three.
- **Leaf accuracy**: exact leaf, over leaf-scored cases only.
- **Classes**: `exact_leaf`, `sibling_leaf_same_root`, `wrong_root`,
  `root_correct_leaf_unscored`, `not_completed`.
- **Accuracy against coverage** at fixed thresholds: stage-1 top probability and
  top-to-second ratio for the root; stage-2 top probability and ratio for the
  leaf; the fit Noul for the root. Probability thresholds 0.5, 0.6, 0.7, 0.8,
  0.9, 0.95; ratios 1.5, 2, 3, 5, 10; fit 0.5, 0.7, 0.9.
- **Back-off**: at each probability threshold, write the leaf when its stage-2
  probability reaches it, otherwise the stage-1 root when its probability
  reaches it, otherwise abstain.

Envelope stratum: counts only (how often stage 1 or the fit Noul would have
been confident). Probes: hijacks (the top root becomes the injected root when the
clean copy's was not), unchanged top roots, and the mean shift in the injected
root's probability. Usage: input tokens per stage and the largest ratio of used
to reserved tokens.

## What counts as a good result (preregistered)

All of these, scored by `round4_scoring.py` from the frozen scoring freeze:

| Check | Requirement |
| --- | --- |
| Complete | all 60 cases complete; no failure or unknown |
| Root top-1 | item-stratum root accuracy at least 0.80 |
| Gold root in top 3 | at least 0.95 (stage 2 cannot recover otherwise) |
| Leaf top-1 | leaf accuracy at least 0.50 |
| Leaf operating point | some preregistered stage-2 threshold gives selective leaf accuracy at least 0.90 at coverage at least 0.30 |
| Root operating point | some preregistered stage-1 threshold gives selective root accuracy at least 0.95 at coverage at least 0.60 |
| Injection | zero stage-1 hijacks across the probes |

For reference, round 3 got the root right in 15 of 19 writes and the exact leaf
in 4 of 20 cases; the owner agrees with himself 98% at root and 81% at leaf on
identical text. With about 41 leaf-scored cases, a leaf accuracy of 0.50 has a
95% interval of roughly 0.35 to 0.65.

Reading the result:

- Passing is pilot evidence for the request shape, not rollout evidence, and not
  evidence for any threshold: the operating-point checks pick the best of six
  thresholds on this cohort, so a chosen threshold needs a fresh cohort.
- If root accuracy passes but leaf accuracy fails, the next step is the owner's
  written "includes / not" leaf definitions (research recommendation R2), not
  more request changes.
- Any hijack means the injection posture is not sufficient for automatic writes.
- The envelope stratum cannot pass or fail. It shows how often an automatic
  write would land on an item root for something the owner filed under an
  envelope.

## Sequence

Build the packet (`round4_cohort.py`), `offline-plan`, `live-run --dry-run`. The
owner writes the approval file; the lead runs `live-run --send-authorized`. Then
`journal_exchanges.py`, `offline-replay-v4` and `round4_scoring.py`. Stop on any
failure. Never resend, repair a journal or relabel after predictions. Once
inspected, this cohort is consumed.

## Round-3 re-score

The corrected round-3 scorer (`aggregate_scoring.py --placeholder-leaf N/A
--placeholder-leaf Unknown`) re-scored the frozen round-3 journal as a regression
check. Gate verdicts are unchanged, because a sibling leaf is still not the
owner's decision. All 11, 9 and 6 former "harmful specificity" writes are
`sibling_leaf_same_root`. Four of the 20 golds are N/A leaves, so leaf accuracy
is over 16 cases:

| Arm | Root accuracy | Root, of writes | Leaf accuracy | Exact / sibling / wrong root / abstained |
| --- | --- | --- | --- | --- |
| Baseline (fit >= 0.7) | 15/20 | 15/19 | 4/16 | 4 / 11 / 4 / 1 |
| Selected path (>= 0.7) | 13/20 | 13/16 | 4/16 | 4 / 9 / 3 / 4 |
| Candidate (0.9 and 0.8) | 10/20 | 10/12 | 4/16 | 4 / 6 / 2 / 8 |
