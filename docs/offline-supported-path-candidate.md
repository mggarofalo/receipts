# Offline supported-path candidate

This experimental slice is `offline-supported-paths-v1`, separate from the actual production `classification-questions-v5` provider. Nothing registers or calls it in the application. It performs no HTTP, credential access, label generation, receipt mutation, taxonomy mutation, or UI flow.

`SupportedCategoryCandidate.Plan` accepts a supplied shortlist of existing IDs plus a complete, versioned taxonomy snapshot. The plan preserves the description, optional store, optional product code, category definitions, and full ancestor paths. Code expands the shortlist to include each ancestor. Bounds are explicit: 32 shortlist entries, 128 expanded paths, 1,024 taxonomy categories, and depth 16. Oversize or malformed input fails; the plan never truncates evidence or taxonomy. Collections are snapshotted and exposed read-only.

The four question definitions live beside the reducer. They describe typed Noul judgments rather than an HTTP schema. Each path receives independent membership-support and context-sufficiency judgments; every question must receive its own full path context and the shared evidence rule. Global judgments separately establish purchase evidence and whether a plausible taxonomy category is absent from the verified paths. Uninterpreted codes and store inventory alone do not establish purchased goods. Questions do not depend on each other's answers.

The reducer validates the revision, exact answer IDs, and all finite [0,1] probabilities before evaluating any outcome. Invalid/missing answers are failures, distinct from valid abstention. Probabilities >= 0.7 support a statement, <= 0.3 reject it, and the middle remains unresolved. These are untuned starting thresholds, not an automatic-assignment safety claim.

Selection requires purchase evidence >= 0.7 and unlisted-alternative probability <= 0.3. A selectable category and every ancestor must have support and sufficient context >= 0.7. Every verified path with support > 0.3 remains a possible rival, including paths with insufficient context. Code chooses the deepest supported category compatible with every rival. Sibling conflict falls back to an explicitly supported common ancestor; incompatible roots abstain. Support is never inferred upward from children. The returned decision carries an existing ID/path or an abstention reason, without a fabricated path confidence/probability.

This is an experimental policy with offline adapters, not a complete categorization pipeline. The [candidate parity tool](../tools/Receipts.JevCandidateParity/README.md) now includes `CandidateWire.Prepare` and `CandidateWireRequest.ParseResponse`: deterministic typed Noul requests, hash-bound exact plan/question IDs, strict bounded raw response validation, token usage capture and mapping into this reducer. The wire API requires the explicitly pinned `jev-1.14.0` model and rejects aliases. It creates no HTTP client, reads no credentials and has no production registration or assignment authorization. The existing CLI still evaluates supplied typed judgments; the wire API is C# only.

There is still no shortlist retrieval, transport, live accuracy evidence or production-v5 comparison. Byte bounds do not enforce the model's 64k request and 32k per-question token limits. A future evaluator must enforce those token limits before any provider dispatch, retain exact raw requests/responses and model provenance, and keep transport failures distinct from abstention. The offline adapter is not provider-ready and supplies no proof that responses originated from TypeSafe. Coverage checks may be conservative when omitted descendants would still fit an explicitly supported broad category; this has not been tuned away.

The synthetic tests establish reducer behavior, offline wire mapping/binding and input boundaries only. They do not establish whether Jev supplies accurate judgments, whether retrieval includes the right category, or whether the coverage judgment detects omitted alternatives. No consumed holdout or private receipt dataset was read for this slice. Evaluate any completed pipeline against actual production v5 on a newly frozen, untouched evaluation set before making a deployment decision.

Run the focused suite with:

```powershell
dotnet test tests/Application.Tests/Application.Tests.csproj --filter FullyQualifiedName~SupportedCategoryCandidateTests
```
