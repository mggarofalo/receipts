# No automatic application of Jev catalog matches

Date: 2026-11-01. Status: accepted. Tracking: RECEIPTS-1093.

## Context

[Catalog normalization](../chatgpt-normalization.md) says model suggestions never approve identities automatically.
Category assistance reversed the equivalent rule for strong matches (#845,
[Jev suggestion adapter](../jev-suggestion-adapter.md)). Catalog identity is a finer judgment than category: a match
merges purchase history under one product name, including its brand, variant and package size. So it needs its own
decision, made on catalog evidence.

The evidence is the RECEIPTS-1091 offline evaluation of policy
[`catalog-matching-questions-v1`](../jev-catalog-matching.md) (#863,
[results](../normalization-evaluation/catalog-jev-1091.md),
[preregistration](../normalization-evaluation/catalog-jev-1091-preregistration.md)). It covered 240 cases and 498 Jev
requests at the policy's provisional thresholds:

| Gate | Result |
| --- | --- |
| Injection | **Failed.** 7 of 20 probes hijacked. In 5 of them, a non-product line or a different product became a match suggestion on the candidate an instruction named. Four of the five had fit above 0.9. |
| Wrong variant | **Failed.** 4 of 40 hard negatives became match suggestions on a sibling, for example a half gallon matched to a gallon. |
| Proxy coverage | **Failed.** 12 of 150 (8%). |
| Abstention | Passed: 30 of 30 without a match. |
| Proxy precision | Passed: 12 of 12, but with n = 12 the 95% lower bound is about 75%. |

No threshold setting tested, inside the swept grid or above it, removes the injected matches while keeping any correct
proxy match.

## Decision

**No.** Jev catalog matches are never applied automatically, at any threshold or Q1/Q2/Q3 combination. Under v1 no
combination qualifies: the worst failures, the injected matches, sit at the highest probabilities the model gives,
which is exactly where an automatic rule would act.

The decision covers both possible scopes. Jev would write neither a **scoped source mapping** (which would also apply to
every future purchase from that source) nor **individual purchases**. Every catalog identity still goes through the
scoped historical preview and a person's approval, as before. RECEIPTS-1092 (the provider) and RECEIPTS-1101 (its
client) are parked, so Jev does not suggest catalog matches either.

## When to reconsider

Only after a new policy version (`catalog-matching-questions-v2` or later) passes a fresh, preregistered evaluation
with untouched cases. That evaluation would need to show zero injected matches, no wrong-variant matches, and a
calibrated operating point. A proposal at that time must name:

- the thresholds;
- the qualifying Q1/Q2/Q3 combination;
- whether it would write a scoped source mapping or only individual purchases;
- its undo, its activity log and its administrator setting, as category assistance has.

Re-tuning v1's thresholds is not grounds to reopen this decision.

## Consequences

- No follow-up implementation item. Nothing in the application changes.
- [Catalog normalization](../chatgpt-normalization.md) and [Jev catalog matching](../jev-catalog-matching.md) link here.
- The v1 response parser and decision code stay in the codebase. They are tested, and a v2 evaluation would reuse the
  harness. They carry no production caller.
