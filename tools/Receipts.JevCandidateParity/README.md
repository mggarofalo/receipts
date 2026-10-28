# Offline experimental candidate plan and decision parity

This adapter invokes `SupportedCategoryCandidate.Plan` and `SupportedCategoryCandidate.Decide`
from Application directly. All question wording, criteria, evidence rules and thresholds
come from that candidate. No candidate algorithm is copied. It creates no application
host, provider, HTTP client, database or inference request, reads no configuration or
credentials, and cannot activate a policy or persist assignments.

From the repository root:

```powershell
dotnet run --project tools/Receipts.JevCandidateParity -- tools/Receipts.JevCandidateParity/fixtures/synthetic-fruit.json
dotnet test tests/Application.Tests/Application.Tests.csproj --filter FullyQualifiedName~JevCandidateParityTests
```

Build/restore may access package feeds. After building, obtain clean JSON stdout
without build/restore by running the DLL:

```powershell
dotnet tools/Receipts.JevCandidateParity/bin/Debug/net10.0/Receipts.JevCandidateParity.dll tools/Receipts.JevCandidateParity/fixtures/synthetic-fruit.json
```

Supply an explicit UTF-8 JSON fixture with `id`, `purchase`, `taxonomyRevision`,
`taxonomy`, `shortlistedIds` and a typed `judgment`, as shown in the entirely synthetic
example. Use independently sanitized or synthetic data: the adapter does not sanitize
or certify it. Output contains purchase text and the full taxonomy; handle it like
the input. The CLI reads only the named file, with an 8 MiB input bound. Unknown JSON
properties and missing constructor fields (including every probability) are rejected
to catch schema mistakes. Include nullable fields explicitly as `null` when absent.
Candidate input bounds still apply.

Judgments must cover exactly the expanded verification path IDs, including each
ancestor. In the example `apple` expands to `apple` and `food`; `tools` remains in
the taxonomy for the supplied unlisted-alternative judgment but is not a verified
path. The caller supplies the shortlist; this adapter performs no retrieval,
shortlist generation or recall measurement. It cannot establish that the shortlist
covers the correct category. A low supplied unlisted-alternative probability is
an assumption of this offline evaluation, not independent proof of completeness.

## Fingerprint contract

The candidate has no TypeSafe wire serializer or model selection. This adapter
defines `candidate-offline-plan-v1`, an **offline snapshot**, not a provider request.
The output `plan` contains the format, candidate policy version, evidence rule,
support/rejection thresholds, four candidate question/criterion templates and the
actual immutable plan (purchase, taxonomy revision, taxonomy and expanded paths).
Path templates are interpreted against each full path in the plan; they are not
bound into API question IDs or a wire payload here.

`planSha256` hashes the compact UTF-8 camelCase JSON serialization of that output
`plan` object using this adapter's read-only `JsonSerializerOptions`.
`decisionSha256` hashes the same serialization of the complete output `decision`.
Both are lowercase SHA-256 hex. Fixture id, filename, timestamps, environment and
judgment inputs are excluded. Judgment changes affect the decision hash only if
the resulting decision changes; these hashes are not a full evaluation audit log.
Keep the original fixture separately when reproducing an evaluation.

Preserve taxonomy input order: it is retained in the snapshot even if two orders
have equivalent decision semantics. Expanded paths are ordered by the candidate.
Shortlist duplicates/order that expand to the same paths produce the same snapshot.
These are .NET JSON fingerprints, not canonical cross-language hashes. Record the
git revision and SDK/runtime alongside comparisons. Version labels alone do not
identify the code. Matching hashes imply neither semantic equivalence between
different policies nor model accuracy.

Successful execution exits 0 for selection, abstention and candidate
`invalid-judgment` failures, recording them distinctly. Invalid plan inputs,
malformed/oversized/unreadable files and invalid CLI usage exit 2. Error messages
do not echo fixture content or exception text.

This is experimental plan/reducer parity only. The separate production parity
adapter calls production question serialization and policy; its question hash and
this plan hash have different contracts and must not be compared as equal requests.
This adapter does not establish production/provider parity, raw response parsing,
transport behavior, authorization, model quality, shortlist recall or activation
readiness. There is no inference capability or production registration.
