# RECEIPTS-1056 evaluation outcome

The v5 candidate is approved for bounded, user-confirmed category suggestions. Automatic classification is not approved by this evaluation. Manual entry and approved deterministic source mappings remain independent of the provider; suggestions must not create a review chore for every receipt item.

This is a designed synthetic challenge set against a constructed nine-category taxonomy, not measured production accuracy. Category names and definitions in the application remain user-controlled. Parent fallback and the distinction between plain milk and cultured dairy were made explicit in the constructed v5 definitions; this does not impose those distinctions on user data.

| Frozen run | Agreement | Result | Measured input cost |
| --- | --- | --- | --- |
| v2 | 31/32 | No-go: instruction-only text incorrectly classified | $0.001991472 |
| v3 | 30/32 | No-go: two false abstentions failed broad/instruction gates | $0.002207142 |
| v4 | 31/32 | No-go: kefir boundary mismatch; subsequent review identified ambiguous constructed definitions | $0.002113524 |
| v5 | 32/32 | All frozen assisted-suggestion gates passed | $0.002372370 |

All historical results and frozen policies remain unchanged. Each subsequent held-out set used fresh source strings; earlier sets became tuning material. v5 passed 23/23 category suggestions, 8/8 direct-parent selections, 9/9 expected abstentions and 10/10 instruction-bearing cases. No harmful descendants, wrong branches, failed requests, unknown usage or model drift were observed. Its latency was p95 114 ms, maximum 178 ms. Total testing: 128 requests, $0.008684508 measured input cost; output tokens are free under the tested model pricing.

Independent review verified labels against definitions, frozen snapshot hashes, exact payload parity, historical provenance and 28 offline tests. The model was jev-1.14.0, with unchanged fit thresholds: <=0.3 no-fit, between 0.3 and 0.7 unresolved, >=0.7 eligible for a suggestion requiring explicit acceptance.

Adapter delivery still requires provider-neutral policy, exact ID/revision validation, bounded context and concurrency, cancellation and error handling, explicit inference opt-in, redacted usage accounting, and absent-provider UI behavior. Representative production-size taxonomy and actual adapter contract evidence belong to integration acceptance. No confidence, calibration or resistance-to-arbitrary-attacks claim follows from these small synthetic sets.

Private workstation evidence is retained outside git under receipts-017-artifacts/1056: independent reviews, exact payloads/responses, results-analysis-v2/v3/v4/v5 (v2 uses results-analysis.json), and usage records. No credentials or private receipts were sent by these evaluation runs.
