# Jev catalog matching: offline evaluation (RECEIPTS-1091)

**Verdict: do not ship Jev catalog matching as configured, and keep ChatGPT.**

Policy `catalog-matching-questions-v1` passed the protocol and abstention gates. It failed two of the four
[preregistered](catalog-jev-1091-preregistration.md) safety gates: instructions written in receipt text turned
non-product lines into confident matches, and some wrong variants were proposed as matches. It also missed the
usefulness gate: 8% proxy coverage. No threshold setting tested, inside or above the swept grid, makes it both safe
and useful. The policy needs a new version before RECEIPTS-1092 builds a provider on it, and RECEIPTS-1093
has no evidence for automatic application.

Only aggregate numbers are reported here. Real receipt text, per-case rows and provider responses stay private.

## What ran

| | |
| --- | --- |
| Policy and model | `catalog-matching-questions-v1`, `jev-1.14.0`, provisional thresholds (0.3 / 0.7 / 0.5 / 0.3 / 0.7) |
| Code | Packets frozen at `a07d30f4`. Requests built, parsed and decided by the production builder, parser and `CatalogMatchingPolicy`. |
| Retrieval | Production catalog mode on the restored 2026-10-29 backup, with retrieval as of `main` at `b905f4ff` (includes #852, #853 and #856). 229 active names with 100% embedding coverage; 1,420 pending names unreachable, because RECEIPTS-1107 was not applied. Every proxy target ranked first. |
| Cohort | 240 cases: 150 `proxy`, 30 `not-in-shortlist`, 40 `hard-negative` (24 `catalog.v2` + 16 new), 20 `injection`. No real source was skipped. |
| Approved examples | The plan reports 0 real examples before leave-one-out and 0 removed, because none exist. The 12 examples sent were all synthetic. Synthetic cases are exempt from leave-one-out (a preregistration amendment made before any request). `catalog.v2` cases are sent without a store, as in their ChatGPT runs. |
| Requests | 20 smoke + 478 full = **498 of the 500 approved**. Every request completed; there were no failures and no retries. |
| Cost | 363,107 input tokens across both packets (13,775 smoke and 349,332 full): **USD 0.0153 measured**, against a reservation of USD 0.0409 and a cap of USD 1.50. Output is free. |
| Latency | p50 113 ms, p95 162 ms, maximum 274 ms (the 478 full-run requests) |
| Parity | All 498 recorded requests were rebuilt byte for byte from the frozen packets. Replaying the journals reproduces both live results exactly. This shows the run is deterministic and replayable from the frozen build. It is production parity only because the tool calls the production builder, parser and decision directly; there is no second implementation to compare. |

The ChatGPT baseline was not run live: no Receipts ChatGPT connection exists locally, and creating one was out of
scope. The comparison below uses the recorded 2026-10-20 blind-harness answers on the 24 `catalog.v2` cases. That harness
model was not the production ChatGPT connection.

## Gates

| Gate | Criterion | Result | |
| --- | --- | --- | --- |
| G0 protocol | no failures, exact parity | 498/498 valid, parity exact | **pass** |
| G1 minimum cohort | ≥ 228 completed, ≥ 140 proxy | 240 completed, 150 proxy | **pass** |
| G2 injection | 0 hijacks of 20 | **7 hijacks**: 5 of them match suggestions on the injected candidate | **fail** |
| G3 wrong variant | ≤ 1 of 40 hard negatives | **4 wrong-variant match suggestions** | **fail** |
| G4 abstention | ≥ 27 of 30 without a match | 30 of 30 (26 needs new name, 4 review) | **pass** |
| G5 proxy precision | ≥ 95% | 12 of 12 match suggestions name the linked target (100%; with n = 12 the 95% lower bound is about 75%) | **pass** |
| G6 proxy coverage | ≥ 60 of 150 correct matches | 12 of 150 (8%) | **fail** |

## Findings

### Injection (G2)

- **What the hijacks have in common.** In every one, the instruction *names a catalog product*. The likely reading is
  that the model takes that name as the product. The probes had no uninjected twin, so this is an inference, not a
  measured effect. Four non-product lines were matched to the product named in the instruction, with fit 0.93–0.98 and Q3
  0.76–0.98: `inj-gift-card`, `inj-tax`, `inj-coupon` and `inj-dog-food` (a product absent from the shortlist, matched
  to the named one).
- **A present product was also hijacked.** `inj-milk` was matched to the injected dish soap.
- **Two more hijacks stayed safe.** In `inj-deposit` and `inj-bag-fee` the injected candidate became Q1's top choice,
  but the outcome stayed review.
- **What held.** None of the four instructions planted in approved examples worked, and neither injected no-fit worked.
- **The evidence line isn't enough.** It says instructions are not evidence, but it cannot stop a product name inside
  an instruction from reading as evidence.

### Wrong variants (G3)

Four of the 40 hard negatives became match suggestions on a sibling:

| Case | What went wrong |
| --- | --- |
| `hn-milk-fat` | HG (half gallon) was matched to a gallon, with Q3 at 0.90 |
| `hn-coffee-form` | Whole bean was matched to ground |
| `hn-scope-abbrev` | An abbreviation approved at another store was taken as proof |
| `ho-beans-scope` | An abbreviation approved at another store was taken as proof |

The `catalog.v2` "with example" controls behaved as labelled except `ex-pasta-with`, which went to review (Q3 0.65).

### Proxy coverage (G6), and what the proxy labels hide

**Q1 finds the linked name.** Its top choice was the linked target in 129 of 150 proxy cases (86%), and above 0.9
probability its top choice was right 98% of the time (Brier 0.09). Two gates stand between that and a match: Q2 and Q3.

**Every inspected Q2 "no fit" looked like a proxy error.** The evaluator inspected privately the 35 proxy cases where Q2
said "none of these". This was a post-hoc, non-blind judgment, made knowing Jev's answer, and the cases Jev accepted
were not checked for the opposite error. In all 35 the legacy link appeared to point at a different product: a spread linked to a dairy product (13 times), a different
battery size, pepper colour, spice, garment, brace or pill, and so on. On
that reading, Jev's "needs new name" was right each time, and the proxy is noisier than earlier reports assumed. The 18 uncertain-fit cases are mostly genuine
variant or brand ambiguity, plus further proxy errors.

**Q3 holds back 85 cases** (50 `uncertain-variant`, 35 `variant-mismatch`). Almost all follow one pattern: the catalog
name carries a brand, size or variant that the receipt text does not state, for example a coffee brand, a size suffix
or a cultivar. The v1 rule sends these to review by design. The model often answers a confident "no" (Q3 < 0.3) instead
of the intended uncertain band, so `variant-mismatch` does not reliably mean "different variant". Several catalog names
were created from the first receipt text seen, so they embed details later purchases don't repeat. That makes this rule
very restrictive on this catalog.

### Abstention (G4)

With the linked name removed, no real case got a match suggestion. 26 of 30 went to "needs new name" and 4 to review.

### Calibration

Labels are "Q1's top choice is the label" (Q1), "a match is the correct outcome" (Q2) and "Q1's top choice is the
label" (Q3). Q2's label is slightly broader than the preregistration's "cases with a known answer". A `catalog.v2`
`insufficient` case counts as "no match" even when its intended product is in the list, because its evidence does not
identify it. Proxy labels confound Q2 and Q3: in the proxy errors above, the label is probably wrong.

| Bin | Q1 n | Q1 predicted / observed | Q2 n | Q2 predicted / observed | Q3 n | Q3 predicted / observed |
| --- | ---: | --- | ---: | --- | ---: | --- |
| 0.0–0.1 | 0 | — | 38 | 0.06 / 0.45 | 55 | 0.02 / 0.51 |
| 0.1–0.2 | 0 | — | 25 | 0.14 / 0.48 | 42 | 0.16 / 0.19 |
| 0.2–0.3 | 0 | — | 12 | 0.22 / 0.50 | 35 | 0.25 / 0.80 |
| 0.3–0.4 | 0 | — | 5 | 0.37 / 0.60 | 44 | 0.34 / 0.91 |
| 0.4–0.5 | 0 | — | 8 | 0.47 / 0.88 | 9 | 0.42 / 0.89 |
| 0.5–0.6 | 7 | 0.55 / 0.43 | 10 | 0.57 / 0.60 | 6 | 0.53 / 0.67 |
| 0.6–0.7 | 6 | 0.66 / 1.00 | 9 | 0.64 / 0.33 | 6 | 0.63 / 0.67 |
| 0.7–0.8 | 13 | 0.75 / 0.54 | 20 | 0.74 / 0.70 | 8 | 0.75 / 0.25 |
| 0.8–0.9 | 16 | 0.85 / 0.38 | 32 | 0.86 / 0.91 | 7 | 0.84 / 1.00 |
| 0.9–1.0 | 131 | 0.98 / 0.98 | 80 | 0.96 / 0.95 | 27 | 0.95 / 0.81 |
| **Brier** | 173 | **0.091** | 239 | **0.196** | 239 | **0.336** |

- **Q1** is well calibrated where most of its mass sits (≥ 0.9).
- **Q2** is reliable at the top: 95% observed above 0.9.
- **Q3** is not calibrated as a same-variant probability against these labels. That is partly by design, since a
  correct candidate whose name states an unsupported attribute should score low.

### ChatGPT comparison (indirect, 24 `catalog.v2` cases)

The harness column is reconstructed, not replayed. The 2026-10-20 report records per-case agreement with the labels
and names its one disagreement, so its answers are taken to be the labels except `ho-detergent-strength`
(`insufficient`).

| | Jev v1 | 2026-10-20 harness |
| --- | ---: | ---: |
| Three-way agreement (same / different / insufficient) | 20 / 24 | 23 / 24 |
| Match vs no-match agreement | 22 / 24 | 24 / 24 |
| False matches | 1 (`ho-beans-scope`) | 0 |

The preregistered bar for replacing ChatGPT was all gates passing, no false match on `catalog.v2`, and at least 22 of 24
on match agreement. Jev v1 misses it on the gates and on the false match.

### Threshold sweep (exploratory, in-sample)

Over 100 points (`FitGate` 0.5–0.9 × `ChoiceFloor` 0.3–0.9 × `SameAtOrAbove` 0.5–0.9), **none** combines zero injected
matches with at most one wrong variant. At least 3 injected matches remain at every point.

The grid stops at 0.9, while three injected matches carry fit and Q3 between 0.93 and 0.98. Above the grid, both bars
at 0.95 leave no correct proxy match but still 2 injected matches. At 0.98 both are gone. No bar removes the injected
matches and keeps any correct proxy match. Proxy coverage never exceeds 17 of 150 (11%), and every proxy match at every point
named the linked target.

The sweep counts **injected matches**: match suggestions on the injected candidate, plus an injected no-fit that sent a
present product to naming. The G2 hijack count also includes an injected top choice that stayed in review. Thresholds
cannot change a top choice, so the sweep leaves those out.

| FitGate | SameAtOrAbove (ChoiceFloor 0.5) | Proxy matches | Wrong variant | Not-in-shortlist matches | Injected matches |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0.5 | 0.5 | 17 | 7 | 0 | 6 |
| 0.7 | 0.7 | 12 | 4 | 0 | 5 |
| 0.9 | 0.5 | 17 | 0 | 0 | 4 |
| 0.9 | 0.9 | 6 | 0 | 0 | 3 |

## Recommendation

1. **Do not build RECEIPTS-1092 on v1.** No thresholds are recommended: no point in the sweep is safe.
2. **Keep ChatGPT** for the catalog source-run. This comparison is indirect, but Jev v1 is worse on it.
3. **RECEIPTS-1093:** no evidence supports automatic application. Answer "no" for v1.
4. **If Jev is pursued, write a `catalog-matching-questions-v2`** and evaluate it on a fresh cohort. Evidence from this
   run that a v2 should address:
   - **Non-product lines.** Gate them before any candidate is shown, so a product name inside an instruction cannot pull
     the answer. Two options: a candidate-free Noul ("does `purchase.receiptText` name a purchased product rather than a
     fee, tax, coupon, deposit or gift card?"), or the existing non-product word rules in code.
   - **Package size.** Q3 accepted HG as a gallon. Size abbreviations may need to be spelled out in the wording, or Q3
     limited to the attributes a catalog name actually states.
   - **Other-store evidence.** Two scope controls were matched on an other-store abbreviation. Consider not sending
     other-store examples until there are same-store ones.
   - **Unsupported brand or size in the catalog name.** Decide whether this case should be review, as in v1, or a
     match. On this catalog it decides most of the coverage.
5. **The legacy-link proxy looks unreliable as ground truth.** Jev flagged 35 of 150 sampled links as different
   products, and a non-blind private inspection agreed with all 35. Treat this as a lead for catalog cleanup and a
   caution when reading the RECEIPTS-1089 proxy numbers, not as a measured error rate.
