# Tasks

## 1. Follow-up issues

- [x] 1.1 File a Plane issue for each known code gap in design.md, linking the spec requirement it must meet. That is one issue each for: the editor's depth limit, legacy labels blocking automation, restructure-cleared eligibility, Admin-only cleanup and historical requests, and the dashboard's Top category. Verify by listing the five new issue ids in the PR description.
- [x] 1.2 File a Plane issue for implementing `envelope-attribution`. Its scope is the fill-only hook, Automatic for any source, the "Also move to X?" offer, pushed-receipt warnings, and removing the split comparison's current-hierarchy option, marked breaking. Verify the issue id is in the PR description.
- [x] 1.3 File two Plane ideas: remembering a mapping at receipt entry, and targeted correction of one non-split YNAB transaction (confirm the YNAB API on a test budget first). Verify both ids are in the PR description.

## 2. Decision records

- [x] 2.1 Write `docs/decisions/2026-11-15-jev-strong-match-automatic-writes.md` as design.md describes, and mark the automatic-action gate in `docs/decisions/2026-10-28-historical-automatic-classification.md` as superseded, linking to the new ADR. Verify that both files link to each other.
- [x] 2.2 Write `docs/decisions/2026-11-15-envelope-fixed-once-recorded.md`. It states the rule, the alternatives rejected, the "never rewrite YNAB history" principle and the no-delete-and-recreate rule for splits, and links the implementation issue from 1.2. Verify that it names the `envelope-attribution` spec.

## 3. Doc fixes

Lines that describe current behaviour the code still has stay as they are, apart from a pointer to the spec and its issue.

- [x] 3.1 In `docs/classification-contract.md`:
  - Fix the stale status lines (:3-6).
  - Replace "do not impose depth" (:24-26) with the two-level limit.
  - Make the source list match the six sources (:49-52).
  - Drop "protected counts" and "explicit inclusion" (:107-109, :119-120).
  - Mark the legacy-label lock-out as not intended (:204-212).
  - Change Top category to follow the view (:229-230).
  - Remove superseded text (:137, :246-255).

  Link each change to its spec or issue. Verify with `grep -n "depth\|protected\|retrospective operation" docs/classification-contract.md` that no line contradicts the specs.
- [x] 3.2 Fix these stale lines:
  - `docs/taxonomy-revisions.md:13`: depth.
  - `docs/historical-reclassification.md:5-11` and `:50`: activity producers, and "Jev unimplemented".
  - `docs/approved-category-mappings.md:54-68`: the review UI requirement is met by the Remember panel.
  - `docs/category-reporting-foundation.md:6` and `:157`: pending consumers.
  - `docs/envelopes.md:41-42`: "legacy-label save".
  - `docs/decisions/2026-10-25-versioned-classification.md:3`: status.

  Verify each file by re-reading the edited lines against the matching spec requirement.
- [x] 3.3 Add one line to the classification section of `docs/envelopes.md` stating that `envelope-attribution` is the intended rule and that implementation is tracked by the 1.2 issue. Verify that the doc's link resolves to the spec path.

## 4. Verification

- [x] 4.1 Run `openspec validate receipts-1118-seed-classification-specs --strict` and confirm it passes.
- [x] 4.2 Commit the change and confirm the pre-commit hook passes.

## Workflow follow-up

- Open the PR `docs(application): seed classification and envelope specs` against `main`. Don't merge it.
- After review, archive on this branch as the last commit before squash-merge, following the config's archive guidance. Confirm that both ADRs exist first.
- Move RECEIPTS-1118 to Done after merge.
