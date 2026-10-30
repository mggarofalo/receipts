# RECEIPTS-1076: owner-label holdout (round 3)

> **Historical.** Round 3 ran on 2026-10-29. Its scorer called a sibling leaf
> under the owner's root "harmful specificity"; the corrected re-score and the
> current protocol are in [round 4](AUTO-EVAL-NEXT-V4.md).

Prepared 2026-10-29 on macOS after the Windows workstation holding the
[first pilot](AUTO-EVAL-NEXT.md) and the [second-round packet](AUTO-EVAL-NEXT-V2.md)
was lost. Their tooling was recovered from the session transcripts and is
committed here. Their private packets, labels, prediction journals and spend
ledger were not recoverable, so the second-round packet cannot be run: its
frozen hashes and labels are gone. This round replaces it. No provider calls,
production changes or automatic writes are authorized by this document.

## What changed from rounds 1 and 2

Rounds 1 and 2 used blind agent labels (two annotators plus an adjudicator) and
explicitly excluded historical assignments as gold. Round 1's annotators
disagreed initially on 15 of 24 cases. The owner has since directed that labels
come from his existing decisions in the data. This round therefore scores
against the owner's own category choices. It does not reuse any agent label.

## Labels: derivation from the backup

`backup_cohort.py` opens a portable SQLite backup read-only and derives labels
only from data the owner entered:

1. Every receipt item carries one current taxonomy assignment. Only owner
   sources count: `legacy_import` (the owner's own category and subcategory
   entries, resolved by exact name when nested categories were introduced),
   `legacy_write` and `manual`. Automatic sources such as `approved_mapping`
   or `template` make the family ineligible (`non_owner`). Nothing is inferred.
   In the 2026-10-29 backup every assignment is `legacy_import`.
2. Items are grouped into **families**, the leakage unit: the union of exact
   case-folded, whitespace-collapsed description text and the application's
   normalized-description identity. Item codes are not used: generic codes
   chain most items into one component. This is lexical separation, not proof
   of semantic independence.
3. A family is **eligible** only when every item in it has the same current
   assignment. Families whose items the owner filed under different categories
   (`mixed`) or that have an unassigned item (`undecided`) are excluded and
   counted. Gold is the shared category's full path. The root of that path is
   also credited as a useful broad answer, because budgets map to top-level
   categories; intermediate ancestors get no credit.
4. Eligible families are ordered by `sha256(json([seed, familyKey]))`, where
   `familyKey` hashes the sorted member item IDs. The first N are taken. Label
   content is not read for ordering; eligibility (one consistent decision) is
   the only label-dependent filter. Later rounds pass earlier
   `membership.private.json` files with `--exclude`; a family is excluded when
   it shares any exact-text or normalized-identity key with a consumed one, so
   a family that grew in a newer backup is still recognized.
5. Each case sends the family's most frequent description. Ties break by the
   same seeded hash.

Consequences to keep in view:

- The owner never left an item without a subcategory, so there are no gold
  **no-answer** or **broad-only** cases. Those two safety gates are reported as
  `untested`; the [synthetic safety set](synthetic-safety/README.md) covers
  them separately. A passing run is therefore still incomplete evidence.
- Restricting to consistent families favours rarely bought items. That matches
  where Jev matters: frequently bought items are handled by approved mappings.
- The lost rounds' membership cannot be excluded. The two-signal thresholds
  were tuned on 12 development cases drawn from the same purchase history, so a
  small overlap with this cohort is possible and cannot be checked.

## Request and decision path

Requests carry the description and the ordered active taxonomy only, built the
way production builds them (head revision, active nodes ordered by ID, blank
definitions fall back to the name). No store, product code, price, quantity,
date, receipt or item identifier is sent. Round 1 sent the same fields.

Three arms share one sequential paired run:

- **baseline**: the production suggestion with its 0.7 global-fit gate;
- **selected-path**: the production selected-path check (support ≥ 0.7) on every
  baseline assignment; valid baseline abstentions skip the second request;
- **candidate** (the hypothesis under test): write the unchanged category and
  path only when baseline global fit ≥ **0.90** and selected-path support ≥
  **0.80**. These thresholds were derived on development data in round 2; this
  round is their first independent test.

## Budget

The policy reserves 65,536 input tokens at USD 0.042 per million for every
attempted request (USD 0.002752512), with free output. Prior and uncertain
spending is carried at **USD 4.873863040**: the pessimistic USD 4.75 allowance
from round 1 plus round 1's 45-attempt reservation. Round 2 made no calls. The
cumulative ceiling is the owner's USD 5 authorization. Round 1's actual
estimated input cost was USD 0.032 for 45 calls.

For N = 20 cases: at most **40 requests** (20 baseline and up to 20
selected-path), a maximum new reservation of **USD 0.110100480**, combined
USD 4.983963520. The runner refuses a send that would exceed either cap. If the
owner reconciles actual spend from the provider's usage page, a later round may
lower the carry; this one does not.

## Preregistered gates (candidate arm)

All are required. The scorer evaluates them and reports `pass`, `fail` or
`untested` for each:

- all 20 cases complete; zero unresolved labels, failures, unknowns or
  unattempted cases (a stopped run is incomplete even if its prefix looks good);
- at least 10 useful candidate assignments;
- zero incorrect candidate writes: no wrong branch, no harmful specificity, no
  other unsupported assignment;
- retain at least 90% of useful baseline assignments: suppress at most
  `floor(0.1 × baselineUseful)`;
- zero writes on gold no-answer cases and zero unsupported descendants on
  broad-only cases (both `untested` here; see above).

Even a clean pass is a necessary pilot result, not rollout evidence: zero errors
in 10 writes leaves a one-sided 95% error bound of about 25.9%; in 20 writes,
about 13.9%. Once inspected, this cohort is consumed for tuning.

## Sequence

The commands are in the [tool README](../../tools/Receipts.JevProductionParity/README.md)
and the [offline evaluation README](README.md). In order: build the packet from
the backup; `offline-plan`; `live-run --dry-run`; the owner writes the approval
file; `live-run --send-authorized`; `journal_exchanges.py`; `aggregate_scoring.py`.
Stop on any failure. Never resend, repair a journal or relabel after predictions.
